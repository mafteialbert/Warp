using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;
using WarpCore;

namespace Warp
{
    public partial class MainWindow : Window
    {
        private readonly WarpProcessor processor;
        private static readonly SileroVAD vad = new(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res", "silero_vad.onnx"));

        public MainWindow()
        {
            InitializeComponent();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                processor = new(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res", "win64"), ".exe");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                processor = new(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res", "linux"));
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                processor = new(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res", "macos"));
            else
                throw new Exception("Platform is unsupported");


        }

        private void UpdateProgres(string progress, bool append = true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if(append)
                    LogConsole.Text += progress + "\n";
                else 
                    LogConsole.Text = progress + "\n";
                LogConsole.CaretIndex = LogConsole.Text.Length;
            });
        }

        private async void StartButton_Click(object? sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            try
            {
                var openFileResult = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select a file",
                    AllowMultiple = false,
                    FileTypeFilter =
               [
                   new FilePickerFileType("Videos")
                    {
                        Patterns = ["*.mp4", "*.mkv"]
                    }
               ]
                });
                if (openFileResult.Count > 0)
                {
                    var selectedFile = openFileResult[0].Path.LocalPath;
                    UpdateProgres($"Selected file: {selectedFile}");
                    FileInfo videoFile = new(selectedFile);
                    FileInfo outputVideoFile = await processor.ProcessVideoAsync(videoFile,
                        vad,
                        WarpParameters.SimpleThreshold(0.5, 1.2, 4), info => UpdateProgres(info));
                    if (outputVideoFile != null)
                    {
                        var saveFileResult = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                        {
                            Title = "Save File",
                            SuggestedFileName = videoFile.Name.Split(".")[0] + "_warped" + outputVideoFile.Extension,
                        });

                        if (saveFileResult != null)
                        {
                            File.Copy(outputVideoFile.FullName, saveFileResult.Path.LocalPath, true);
                            UpdateProgres($"Copied file: {outputVideoFile.FullName} to: {saveFileResult.Path.LocalPath}");
                        }
                        if(outputVideoFile.Directory!=null)
                        {
                            Directory.Delete(outputVideoFile.Directory.FullName, true);
                            UpdateProgres($"Deleted temporary directory: {outputVideoFile.Directory.FullName}");
                        }
                    }
                }
                UpdateProgres($"Finished!");
            }
            finally
            {
                StartButton.IsEnabled = true;
            }
        }
    }
}