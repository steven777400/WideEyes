using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Camera
{
    public class FFMpegRunner
    {
        private Process writer;
        private CameraDetail detail;
        const string FFMPEG_PARAMS = "-loglevel warning -rtsp_transport tcp -stimeout 5000000 -i \"{0}\" -c copy -n -f segment -strftime 1 -segment_time 900 -segment_format mp4 -reset_timestamps 1 \"{1}\"";
        const string FFMPEG_FILE_PTRN = "%Y-%m-%d_%H-%M.mp4";

        private Action<string> log;

        public FFMpegRunner(CameraDetail detail, Action<string> log)
        {
            this.detail = detail;
            this.log = log;


        }

        public async Task Run(CancellationToken stoppingToken)
        {

            DateTime start = DateTime.Now;
            if (!String.IsNullOrEmpty(detail.FFMpegRecordingSourceURL) && /* If the detail provides info for a recording ... */
                    !String.IsNullOrEmpty(detail.FFMpegPath) &&
                    System.IO.Directory.Exists(detail.StoragePath) &&
                    (writer == null || writer.HasExited)) /* ... and there is not a recording process currently running */
            {
                // prepare and start a new recording process
                

                var titledpath = System.IO.Path.Combine(detail.StoragePath, detail.Title);
                if (!System.IO.Directory.Exists(titledpath))
                    System.IO.Directory.CreateDirectory(titledpath);                

                var fullfilen = System.IO.Path.Combine(titledpath, FFMPEG_FILE_PTRN);

                log("start new process to "+fullfilen);

                var torun = String.Format(FFMPEG_PARAMS, detail.FFMpegRecordingSourceURL, fullfilen);

                var psi = new ProcessStartInfo
                {
                    FileName = detail.FFMpegPath,
                    Arguments = torun,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };

                writer = Process.Start(psi);

                
                while (!stoppingToken.IsCancellationRequested && !writer.HasExited)
                {
                    try
                    {
                        await Task.Delay(1000, stoppingToken);
                    } catch (TaskCanceledException) { }
                }

                if (writer != null && !writer.HasExited)
                {
                    // writer.StandardInput.WriteLine("\x3"); // Ctrl+C
                    writer.StandardInput.WriteLine("q"); // q is for quit
                    await Task.Delay(500);                    
                    writer.Kill();
                }



                log("ran to completion");

            }

        }
    }
}
