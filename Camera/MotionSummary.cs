using AnimatedGif;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.ObjectModel;

namespace Camera
{
    public class MotionSummary
    {        

        public CameraDetail Camera { get; private set; }
        
        public DateTime[] Whens { get; private set; }
        public byte[] GIF { get; private set; }        

        private MotionSummary(CameraDetail detail)
        {
            this.Camera = detail;            
        }

        private DateTime[] RunToStream(Stream str, IEnumerable<CameraFrameEventArgs> frames, CancellationToken stoppingToken)
        {
            List<DateTime> whens = new List<DateTime>();

            List<IDisposable> toDispose = new List<IDisposable>();
            try {
                using (var gif = new AnimatedGif.AnimatedGifCreator(str, 500)) // 500 = ~2 fps
                {
                    foreach (var frame in frames)
                    {
                        var loadms = new MemoryStream(frame.JPEG);
                        toDispose.Add(loadms);

                        var img = new System.Drawing.Bitmap(loadms);
                        toDispose.Add(img);

                        if (stoppingToken.IsCancellationRequested)
                            break;

                        gif.AddFrame(img);
                        whens.Add(frame.When);

                    }
                }
            }
            finally
            {
                foreach (var d in toDispose)
                {
                    d.Dispose();
                }
            }

            return whens.ToArray();
        }

        private IEnumerable<CameraFrameEventArgs> SelectFrames(CameraFrameEventArgs[] source, int targetFrames)
        {
            

            int incr = Math.Max(source.Length / targetFrames, 1);

            for (int i = 0; i < source.Length; i += incr)
                yield return source[i];

        }

        public string DescribeTimeAgo()
        {

            var span = DateTime.Now - Whens.Last();

            if (span < TimeSpan.FromMinutes(2))
                return "Just now";
            else if (span < TimeSpan.FromHours(2))
                return $"{span.TotalMinutes.ToString("N0")} minutes ago";
            else if (span < TimeSpan.FromDays(1))
                return $"{span.TotalHours.ToString("N0")} hours ago";
            else
                return "A day or more ago";
        }

        public static MotionSummary RunToInMemoryGif(CameraDetail detail, CameraFrameEventArgs[] frames, CancellationToken stoppingToken)
        {
            var motion = new MotionSummary(detail);

            var selframes = motion.SelectFrames(frames, 10);
            using (var ms = new MemoryStream())
            {                
                var whens = motion.RunToStream(ms, selframes, stoppingToken);
                using (var br = new BinaryReader(ms))
                {
                    ms.Seek(0, SeekOrigin.Begin);
                    motion.GIF = br.ReadBytes((int)ms.Length);
                }
                motion.Whens = whens.ToArray();
            }
            return motion;
            
        }        

        public static string RunToSummaryMp4(CameraDetail detail, CameraFrameEventArgs[] frames, CancellationToken stoppingToken)
        {

            // output_Kayak_2020.10.04.22.47.35
            // timestamp based on first frame
            // .gif, .json, .mp4
            
            
            var motion = new MotionSummary(detail);
            var initframe = Cv2.ImDecode(frames[0].JPEG, ImreadModes.Unchanged);
            string storagesummary = System.IO.Path.Combine(detail.StoragePath, "summary");
            if (!System.IO.Directory.Exists(storagesummary))
                System.IO.Directory.CreateDirectory(storagesummary);

            var filenamebase = System.IO.Path.Combine(storagesummary, $"output_{detail.Title}_{frames[0].When.ToString("yyyy.MM.dd.HH.mm.ss")}");

            using (var writer = new VideoWriter(filenamebase + ".mp4", FourCC.X264, 1, initframe.Size()))
            {
                foreach (var frame in frames)
                {
                    if (stoppingToken.IsCancellationRequested)
                        return null;

                    using (var mat = Cv2.ImDecode(frame.JPEG, ImreadModes.Unchanged))
                    {
                        writer.Write(mat);
                    }
                        
                }                
            }

            var selframes = motion.SelectFrames(frames, 10);
            using (var gifile = File.Open(filenamebase + ".gif", FileMode.Create))
            {
                if (stoppingToken.IsCancellationRequested)
                    return null;

                var whens = motion.RunToStream(gifile, selframes, stoppingToken);                
                
                motion.Whens = whens.ToArray();
            }

            if (stoppingToken.IsCancellationRequested)
                return null;

            // json ["2020-10-05T07:24:51", "2020-10-05T07:24:52"]
            // we should do better json in the future
            var json = JsonSerializer.Serialize(motion.Whens);
            File.WriteAllText(filenamebase + ".json", json);

            return filenamebase;
            

        }
    }
}
