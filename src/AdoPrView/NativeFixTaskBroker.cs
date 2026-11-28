using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AdoPrView;

/// <summary>One on-demand task per owned run, dispatched once under the current interactive token.</summary>
internal static class NativeFixTaskBroker
{
    internal sealed class DispatchException(string message, Exception inner) : InvalidOperationException(message, inner);
    internal static string TaskName(NativeFixSupervisor.Specification s) => "AdoPrView-Fix-" + s.RunId + "-" + s.Token;
    static string Arguments(NativeFixSupervisor.Specification s) =>
        "--native-fix-supervisor " + NativeFixSupervisor.Quote(NativeFixSupervisor.FilePath(s, "ownership.json"));

    internal static string CurrentSid()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Fix requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
    }

    static void Validate(NativeFixSupervisor.Specification s)
    {
        NativeFixSupervisor.Verify(s);
        if (s.SupervisorLauncher != "task-scheduler" || s.SupervisorExecutable is null ||
            s.SupervisorUserSid != CurrentSid())
            throw new NativeFixSupervisor.OwnershipException("The scheduled Fix launch does not match the current user and immutable supervisor.");
    }

    internal static void Launch(NativeFixSupervisor.Specification s)
    {
        Validate(s);
        var dispatchAttempted = false;
        var registered = false;
        try
        {
            using var scope = new ComScope();
            dynamic service = scope.Connect();
            dynamic folder = scope.Track(service.GetFolder("\\"));
            dynamic definition = scope.Track(service.NewTask(0));
            dynamic registration = scope.Track(definition.RegistrationInfo);
            registration.Description = "ADO PR View owned Fix " + s.RunId + ". No triggers, password or administrator privileges.";
            dynamic principal = scope.Track(definition.Principal);
            principal.UserId = s.SupervisorUserSid;
            principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
            principal.RunLevel = 0; // TASK_RUNLEVEL_LUA
            dynamic settings = scope.Track(definition.Settings);
            settings.Enabled = true;
            settings.AllowDemandStart = true;
            settings.StartWhenAvailable = false;
            settings.ExecutionTimeLimit = "PT0S";
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.RunOnlyIfIdle = false;
            settings.AllowHardTerminate = false;
            settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
            settings.RestartCount = 0;
            dynamic actions = scope.Track(definition.Actions);
            dynamic action = scope.Track(actions.Create(0));
            action.Path = s.SupervisorExecutable;
            action.Arguments = Arguments(s);
            action.WorkingDirectory = s.RunDirectory;
            var acl = $"D:P(A;;FA;;;SY)(A;;FA;;;{s.SupervisorUserSid})";
            // CREATE only, never update/reuse a task. No triggers can dispatch it before this explicit Run.
            dynamic task = scope.Track(folder.RegisterTaskDefinition(TaskName(s), definition, 2,
                s.SupervisorUserSid, null, 3, acl));
            registered = true;
            VerifyTask(scope, task, s);
            NativeFixSupervisor.Write(NativeFixSupervisor.FilePath(s, "scheduler.json"),
                new { Task = TaskName(s), UserSid = s.SupervisorUserSid, DispatchRequested = true });
            dispatchAttempted = true;
            scope.Track(task.Run(null));
        }
        catch (Exception ex)
        {
            if (dispatchAttempted)
                throw new DispatchException("Task Scheduler dispatch was attempted but its result is uncertain. The exact task, work and PR reservation are retained; no retry or backend fallback is allowed. Reconnect or use exact Stop.", ex);
            string? cleanup = null;
            if (registered)
            {
                try { Cleanup(s); }
                catch (Exception failure) when (failure is COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
                { cleanup = " The undispatched task registration could not be removed; inspect this exact run's retained scheduler task."; }
            }
            throw new NativeFixSupervisor.LaunchException(ex.HResult,
                $"Per-user Task Scheduler setup failed before dispatch (HRESULT 0x{ex.HResult:x8}). Check that Task Scheduler is available and permits current-user, interactive-token tasks. No administrator/password/service is used and no task was launched." + cleanup);
        }
    }

    static void VerifyTask(ComScope scope, dynamic task, NativeFixSupervisor.Specification s)
    {
        dynamic definition = scope.Track(task.Definition);
        dynamic principal = scope.Track(definition.Principal);
        dynamic actions = scope.Track(definition.Actions);
        dynamic triggers = scope.Track(definition.Triggers);
        dynamic settings = scope.Track(definition.Settings);
        if (!PrincipalMatches((string)principal.UserId, s.SupervisorUserSid!) || (int)principal.LogonType != 3 ||
            (int)principal.RunLevel != 0 || (int)actions.Count != 1 || (int)triggers.Count != 0)
            throw new NativeFixSupervisor.OwnershipException("The exact scheduled Fix task's principal/actions/triggers changed; it was not dispatched or removed.");
        if ((int)settings.RestartCount != 0 || (bool)settings.StartWhenAvailable ||
            (bool)settings.DisallowStartIfOnBatteries || (bool)settings.StopIfGoingOnBatteries ||
            (bool)settings.RunOnlyIfIdle || !(bool)settings.AllowDemandStart ||
            (string)settings.ExecutionTimeLimit != "PT0S" || (int)settings.MultipleInstances != 2)
            throw new NativeFixSupervisor.OwnershipException("The exact scheduled Fix task's no-replay/lifetime settings changed; it was not dispatched or removed.");
        dynamic action = scope.Track(actions.Item(1));
        if ((int)action.Type != 0 || (string)action.Path != s.SupervisorExecutable ||
            (string)action.Arguments != Arguments(s) || (string)action.WorkingDirectory != s.RunDirectory)
            throw new NativeFixSupervisor.OwnershipException("The exact scheduled Fix task's immutable supervisor/specification changed; it was not dispatched or removed.");
    }

    static bool PrincipalMatches(string user, string sid)
    {
        if (user == sid) return true;
        if (!OperatingSystem.IsWindows()) return false;
        // Scheduler reads back an account name even when registration used its SID.
        try { return new NTAccount(user).Translate(typeof(SecurityIdentifier)) is SecurityIdentifier actual && actual.Value == sid; }
        catch (IdentityNotMappedException) { return false; }
    }

    internal static void Cleanup(NativeFixSupervisor.Specification s)
    {
        Validate(s);
        using var scope = new ComScope();
        dynamic service = scope.Connect();
        dynamic folder = scope.Track(service.GetFolder("\\"));
        dynamic task;
        try { task = scope.Track(folder.GetTask(TaskName(s))); }
        catch (Exception ex) when (ex is COMException or FileNotFoundException && ex.HResult == unchecked((int)0x80070002)) { return; }
        VerifyTask(scope, task, s);
        // Removing registration never substitutes for exact owned-job cancellation.
        folder.DeleteTask(TaskName(s), 0);
    }

    internal static bool Registered(NativeFixSupervisor.Specification s)
    {
        Validate(s);
        using var scope = new ComScope();
        dynamic service = scope.Connect();
        dynamic folder = scope.Track(service.GetFolder("\\"));
        try
        {
            dynamic task = scope.Track(folder.GetTask(TaskName(s)));
            VerifyTask(scope, task, s);
            return true;
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException && ex.HResult == unchecked((int)0x80070002)) { return false; }
    }

    sealed class ComScope : IDisposable
    {
        readonly List<object> objects = [];
        internal dynamic Track(object value) { objects.Add(value); return value; }
        internal dynamic Connect()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var type = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Windows Task Scheduler COM service is unavailable.");
            dynamic service = Track(Activator.CreateInstance(type)!);
            service.Connect();
            return service;
        }
        public void Dispose()
        {
            if (!OperatingSystem.IsWindows()) return;
            for (var i = objects.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(objects[i])) Marshal.ReleaseComObject(objects[i]);
        }
    }
}
