using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AdoPrView;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Windows.Media.Editing;
using Windows.Storage;

static class VideoPlaybackProbe
{
    public static Task RunAsync(string input, string outputRoot)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        input = Path.GetFullPath(input);
        outputRoot = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(outputRoot);
        var thread = new Thread(() =>
        {
            var browser = new WebView2();
            var window = new Window { Title = "ADO PR View - isolated public video playback probe",
                Width = 1280, Height = 1050, Content = browser };
            window.Loaded += async (_, _) =>
            {
                Exception? failure = null;
                CoreWebView2Environment? environment = null;
                try
                {
                    var artifacts = new VideoArtifacts(Path.Combine(outputRoot, "media"));
                    var id = Guid.NewGuid().ToString("N");
                    artifacts.CreateWork(id);
                    File.Copy(input, artifacts.Pending(id));
                    var media = artifacts.Publish(id);
                    var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(input));
                    environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(outputRoot, "webview-profile"),
                        new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
                    await browser.EnsureCoreWebView2Async(environment);
                    var core = browser.CoreWebView2;
                    core.WebResourceResponseReceived += (_, e) =>
                    {
                        if (e.Request.Uri.StartsWith("https://pr-video.local/", StringComparison.Ordinal))
                            Console.WriteLine($"MEDIA RESPONSE {e.Response.StatusCode} {e.Request.Uri}");
                    };
                    core.AddWebResourceRequestedFilter("https://pr-video.local/*", CoreWebView2WebResourceContext.All);
                    core.WebResourceRequested += (_, e) => Console.WriteLine($"MEDIA REQUEST {e.Request.Method} {e.Request.Uri}");
                    core.SetVirtualHostNameToFolderMapping("app.local", Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Deny);
                    VideoMedia.Attach(core, artifacts, ex => done.TrySetException(ex));
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var measured = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.WebMessageReceived += (_, e) =>
                    {
                        var message = e.TryGetWebMessageAsString();
                        if (message == "ready") ready.TrySetResult(); else measured.TrySetResult(message);
                    };
                    core.NewWindowRequested += (_, e) => { e.Handled = true; throw new Exception("Playback attempted external navigation."); };
                    core.Navigate("https://app.local/index.html?mock=1");
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    var state = JsonSerializer.Serialize(new
                    {
                        requestId = id, sequence = 1, status = "rendering", message = "Isolated loading-state probe; generation is not running.",
                        evidence = new[] { "Playback fixture only. Source/model generation is established separately by pilot-result.json." },
                        coverage = Array.Empty<string>(), model = VideoStory.Model,
                    }, Bridge.Json);
                    await core.ExecuteScriptAsync("window.probeState={..." + state + ",key:S.snap.prs[0].key};" +
                        "updateVideo(window.probeState);S.expanded.add(window.probeState.key);renderRows();" +
                        "document.querySelector('.pr-video').scrollIntoView({block:'center'});");
                    await using (var file = File.Create(Path.Combine(outputRoot, "loading.png")))
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                    var complete = JsonSerializer.Serialize(new { path = media.Path, mediaUrl = media.Url,
                        durationSeconds = clip.OriginalDuration.TotalSeconds }, Bridge.Json);
                    await core.ExecuteScriptAsync("""
                        (async()=>{
                          updateVideo({...window.probeState,...COMPLETE,sequence:2,status:'completed',message:'Real MP4 playback probe. No ADO calls or uploads.'});
                          const video=document.querySelector('video[data-video-media]');
                          const event=(name)=>new Promise((resolve,reject)=>{
                            const timer=setTimeout(()=>reject(Error(name+' timeout')),15000);
                            video.addEventListener(name,()=>{clearTimeout(timer);resolve();},{once:true});
                            video.addEventListener('error',()=>{clearTimeout(timer);reject(Error('Media error '+video.error?.code+': '+video.error?.message));},{once:true});
                          });
                          if(video.readyState<1)await event('loadedmetadata');
                          await video.play();
                          while(video.currentTime<.5)await event('timeupdate');
                          video.pause();
                          const seek=event('seeked');video.currentTime=video.duration*.7;await seek;
                          await video.play();const frame=event('timeupdate');await frame;video.pause();
                          const existing=video;renderRows();
                          if(document.querySelector('video[data-video-media]')!==existing)throw Error('Refresh replaced the playing video');
                          const url=video.currentSrc;
                          const range=await fetch(url,{headers:{Range:'bytes=100-199'}});
                          const bad=await fetch(url.replace('.mp4','.json'));
                          const result={duration:video.duration,width:video.videoWidth,height:video.videoHeight,
                            position:video.currentTime,readyState:video.readyState,muted:video.muted,volume:video.volume,
                            audioDecodedBytes:video.webkitAudioDecodedByteCount,videoDecodedFrames:video.webkitDecodedFrameCount,
                            frames:video.getVideoPlaybackQuality().totalVideoFrames,
                            rangeStatus:range.status,rangeLength:(await range.arrayBuffer()).byteLength,
                            contentRange:range.headers.get('Content-Range'),unregisteredStatus:bad.status,
                            preservedPlayer:true};
                          if(result.width!==1280||result.height!==720||result.position<video.duration*.65||
                            !(result.audioDecodedBytes>0)||!(result.frames>2)||result.rangeStatus!==206||
                            result.rangeLength!==100||result.unregisteredStatus!==404)throw Error('Playback evidence failed: '+JSON.stringify(result));
                          video.scrollIntoView({block:'center'});window.chrome.webview.postMessage(JSON.stringify(result));
                        })().catch(error=>window.chrome.webview.postMessage(JSON.stringify({error:error.message})));
                        """.Replace("COMPLETE", complete, StringComparison.Ordinal));
                    var json = await measured.Task.WaitAsync(TimeSpan.FromSeconds(45));
                    await File.WriteAllTextAsync(Path.Combine(outputRoot, "playback.json"), json);
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("error", out var error)) throw new Exception(error.GetString());
                    await using (var file = File.Create(Path.Combine(outputRoot, "inline-player.png")))
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                    Console.WriteLine("ACTUAL WEBVIEW2 PLAYBACK " + json);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (environment is not null) environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
                    try
                    {
                        browser.Dispose(); window.Close();
                        if (environment is not null) await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        if (Directory.Exists(Path.Combine(outputRoot, "webview-profile")))
                            Directory.Delete(Path.Combine(outputRoot, "webview-profile"), true);
                    }
                    catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
                    if (failure is null) done.TrySetResult(); else done.TrySetException(failure);
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            };
            window.Show();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }
}
