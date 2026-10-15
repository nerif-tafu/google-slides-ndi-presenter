using System.Diagnostics;

namespace GoogleSlidesNdiPresenter;

internal static class Program
{
    private static volatile bool _restartRequested;

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // --settings <file> runs with a separate settings file (e.g. a second instance with its
        // own API port and NDI source names).
        var i = Array.FindIndex(args, a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length)
            AppSettings.FilePath = Path.GetFullPath(args[i + 1]);

        var settings = AppSettings.Load();
        var google = new GoogleAccount();
        PresentationLoader.Google = google;
        var controller = new PresentationController(settings);
        var audio = new AudioRouter();
        audio.Configure(settings.AudioOutputDevice, settings.AudioOutputVolume, settings.AudioMonitorDevice, settings.AudioMonitorVolume);
        MainForm? form = null;
        ApiServer api;
        try
        {
            api = ApiServer.StartAsync(controller, settings, audio, google, () =>
            {
                _restartRequested = true;
                form?.BeginInvoke(form.Close);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show("The app couldn't start its control server.\n\n" + ex.Message,
                "Google Slides NDI Presenter", MessageBoxButtons.OK, MessageBoxIcon.Error);
            controller.Dispose();
            audio.Dispose();
            return;
        }

        Application.Run(form = new MainForm(controller, settings, api, audio, google));

        settings.Save();
        api.DisposeAsync().AsTask().GetAwaiter().GetResult();
        controller.Dispose(); // frees the NDI source names and port before a restart
        audio.Dispose();

        if (_restartRequested && Environment.ProcessPath is { } exe)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
    }
}
