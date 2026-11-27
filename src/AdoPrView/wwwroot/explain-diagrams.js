'use strict';

const explanationBodies = new WeakMap();
let explanationDiagramId = 0;
let explanationDiagramsInitialized = false;

function explanationDiagramPlaceholder(source) {
  return `<figure class="explain-diagram">
    <p class="explain-diagram-status small" role="status"><span class="video-spinner" aria-hidden="true"></span>Rendering explanation diagram...</p>
    <div class="explain-diagram-drawing"></div>
    <details class="explain-diagram-source"><summary>Diagram source (Mermaid)</summary><pre><code>${esc(source)}</code></pre></details>
  </figure>`;
}

function explanationDiagramProblem(source) {
  if (source.length > 6000) return 'The diagram exceeds the 6,000-character rendering limit.';
  if (!/^\s*(?:flowchart\s+(?:TB|TD|BT|RL|LR)|graph\s+(?:TB|TD|BT|RL|LR)|sequenceDiagram)\b/.test(source))
    return 'Only Mermaid flowcharts and sequence diagrams are supported.';
  // Keep model-supplied configuration, HTML, links, images and CSS out of the renderer itself.
  if (/%%\s*\{|^\s*---|<(?![-=<])|@\{|https?:|javascript:|data:/im.test(source) ||
      /(?:^|[;\n])\s*(?:click|link|links|style|classDef|class|linkStyle)[ \t]+(?![-=<])\S/m.test(source))
    return 'Diagram configuration, HTML, links, images and custom styles are not allowed.';
  return '';
}

function safeExplanationDiagram(svg, description = '') {
  const clean = DOMPurify.sanitize(svg, {
    ALLOWED_TAGS: ['svg', 'g', 'defs', 'marker', 'path', 'rect', 'circle', 'ellipse', 'polygon', 'polyline',
      'line', 'text', 'tspan', 'title', 'desc'],
    ALLOWED_ATTR: ['id', 'class', 'viewBox', 'xmlns', 'width', 'height', 'x', 'y', 'x1', 'x2', 'y1', 'y2',
      'cx', 'cy', 'r', 'rx', 'ry', 'd', 'points', 'transform', 'markerWidth', 'markerHeight', 'refX', 'refY',
      'orient', 'markerUnits', 'marker-start', 'marker-end', 'text-anchor', 'dominant-baseline', 'dy', 'dx'],
    ALLOW_DATA_ATTR: false, ALLOW_ARIA_ATTR: false,
  });
  const template = document.createElement('template');
  template.innerHTML = clean;
  const root = template.content.firstElementChild;
  if (root?.tagName.toLowerCase() !== 'svg' || !root.querySelector('text'))
    throw new Error('The renderer did not return a readable SVG diagram.');
  const prefix = `explanation-svg-${++explanationDiagramId}-`, ids = new Map();
  for (const node of root.querySelectorAll('[id]')) {
    const old = node.id, id = prefix + ids.size;
    ids.set(old, id);
    node.id = id;
  }
  root.removeAttribute('id');
  // Only known local markers are needed; never retain renderer-owned resource URLs.
  for (const node of root.querySelectorAll('[marker-start], [marker-end]')) {
    for (const name of ['marker-start', 'marker-end']) {
      const value = node.getAttribute(name);
      const local = value?.match(/^url\(#([A-Za-z0-9_-]+)\)$/);
      if (local && ids.has(local[1])) node.setAttribute(name, `url(#${ids.get(local[1])})`);
      else node.removeAttribute(name);
    }
  }
  root.setAttribute('role', 'img');
  const title = root.querySelector('title');
  let desc = root.querySelector('desc');
  if (title?.textContent.trim()) {
    title.id ||= prefix + 'title';
    root.setAttribute('aria-labelledby', title.id);
    if (!desc && description) {
      desc = document.createElementNS('http://www.w3.org/2000/svg', 'desc');
      desc.textContent = description;
      root.prepend(desc);
    }
  } else root.setAttribute('aria-label', `PR explanation diagram. ${description || root.textContent.trim()}`);
  if (desc?.textContent.trim()) {
    desc.id ||= prefix + 'description';
    root.setAttribute('aria-describedby', desc.id);
  }
  return root;
}

function renderExplanationDiagrams() {
  for (const output of document.querySelectorAll('.explain-output')) {
    if (!output.querySelector('figure.explain-diagram')) continue;
    if (!explanationBodies.has(output)) explanationBodies.set(output, output.innerHTML);
    for (const figure of output.querySelectorAll('figure.explain-diagram')) {
      if (figure.dataset.diagramState || figure.closest('[hidden], details:not([open])')) continue;
      figure.dataset.diagramState = 'rendering';
      void renderExplanationDiagram(figure, output);
    }

    document.addEventListener('toggle', renderExplanationDiagrams, true);
  }
}

async function renderExplanationDiagram(figure, output) {
  const status = figure.querySelector('.explain-diagram-status');
  const source = figure.querySelector('code').textContent;
  const drawing = figure.querySelector('.explain-diagram-drawing');
  const work = document.createElement('div');
  work.className = 'explain-diagram-work';
  try {
    const problem = explanationDiagramProblem(source);
    if (problem) throw new Error(problem);
    if (!window.mermaid || !window.DOMPurify) throw new Error('The bundled diagram renderer is unavailable.');
    if (!explanationDiagramsInitialized) {
      mermaid.initialize({
        startOnLoad: false, securityLevel: 'strict', suppressErrorRendering: true,
        htmlLabels: false, flowchart: { htmlLabels: false }, theme: 'base',
        fontFamily: 'Segoe UI, sans-serif', maxTextSize: 6000, maxEdges: 80,
      });
      explanationDiagramsInitialized = true;
    }
    const diagram = await mermaid.mermaidAPI.getDiagramFromText(source);
    let description;
    if (/^\s*sequenceDiagram\b/.test(source)) {
      const signals = new Set(Object.entries(diagram.db.LINETYPE)
        .filter(([name]) => /SOLID|DOTTED/.test(name)).map(([, type]) => type));
      const messages = diagram.db.getMessages().filter(message => signals.has(message.type));
      if (messages.length > 80)
        throw new Error('The diagram exceeds the 80-connection rendering limit.');
      const actors = diagram.db.getActors(), name = id => actors.get(id)?.description || id;
      const bidirectional = new Set([diagram.db.LINETYPE.BIDIRECTIONAL_SOLID, diagram.db.LINETYPE.BIDIRECTIONAL_DOTTED]);
      description = messages.map(message => bidirectional.has(message.type)
        ? `${name(message.from)} exchanges ${message.message} with ${name(message.to)}.`
        : `${name(message.from)} sends ${message.message} to ${name(message.to)}.`).join(' ') ||
        `Participants: ${[...actors.keys()].map(name).join(', ')}.`;
    } else {
      const vertices = diagram.db.getVertices(), name = id => vertices.get(id)?.text || id;
      description = diagram.db.getEdges().map(edge =>
        `${name(edge.start)} connects ${edge.type?.startsWith('double_') ? 'in both directions ' : ''}to ${name(edge.end)}${edge.text ? ': ' + edge.text : ''}.`).join(' ') ||
        `Nodes: ${[...vertices.keys()].map(name).join(', ')}.`;
    }
    if (!figure.isConnected || !output.contains(figure)) return;
    figure.appendChild(work);
    const { svg } = await mermaid.render(`explanation-diagram-${++explanationDiagramId}`, source, work);
    if (!figure.isConnected || !output.contains(figure)) return;
    const rendered = safeExplanationDiagram(svg, description);
    const main = document.querySelector('main'), top = main?.scrollTop;
    const outputTop = output.scrollTop, outputLeft = output.scrollLeft;
    drawing.replaceChildren(rendered);
    figure.dataset.diagramState = 'completed';
    status.hidden = true;
    output.scrollTop = outputTop;
    output.scrollLeft = outputLeft;
    if (main) main.scrollTop = top;
  } catch (error) {
    if (!figure.isConnected || !output.contains(figure)) return;
    figure.dataset.diagramState = 'failed';
    status.classList.add('error');
    status.textContent = `Diagram could not be rendered: ${error.message || 'Invalid Mermaid syntax.'} The completed explanation and diagram source remain available.`;
    figure.querySelector('details').open = true;
  } finally {
    work.remove();
  }
}
