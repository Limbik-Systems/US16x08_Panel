using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using US16x08_AbletonPanel.ViewModels;

namespace US16x08_AbletonPanel;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--self-test"))
        {
            try
            {
                string outDir = e.Args.Length > 1 ? e.Args[1] : AppDomain.CurrentDomain.BaseDirectory;
                Directory.CreateDirectory(outDir);

                var win = MainWindow as MainWindow ?? new MainWindow();
                win.Show();
                await Task.Delay(550); // Let 60 FPS meter simulation and DSP curves render several frames

                CaptureWindowToPng(win, Path.Combine(outDir, "ableton_mixer_tab0.png"));

                if (win.DataContext is MainViewModel vm)
                {
                    vm.ActiveTabIndex = 1;
                    await Task.Delay(200);
                    CaptureWindowToPng(win, Path.Combine(outDir, "ableton_routing_tab1.png"));

                    vm.ActiveTabIndex = 2;
                    await Task.Delay(200);
                    CaptureWindowToPng(win, Path.Combine(outDir, "ableton_inspector_tab2.png"));

                    vm.ActiveTabIndex = 0;
                }

                Console.WriteLine("SELF_TEST_OK");
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("SELF_TEST_ERR: " + ex);
                Shutdown(1);
            }
        }
    }

    private static void CaptureWindowToPng(Window win, string path)
    {
        win.UpdateLayout();
        int w = Math.Max(800, (int)win.ActualWidth);
        int h = Math.Max(600, (int)win.ActualHeight);

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
