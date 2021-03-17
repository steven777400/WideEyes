using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using MailKit.Net.Smtp;
using MimeKit;
using Camera;
using Renci.SshNet;
using System.IO;
using System.Collections.Concurrent;

namespace CameraService
{
    public class Worker : BackgroundService
    {


        public static Camera.CameraDetail[] CameraDetails;


        public static readonly ConcurrentDictionary<string, Camera.CameraFrameEventArgs> LastFrame = new ConcurrentDictionary<string, Camera.CameraFrameEventArgs>();        

        public static readonly ConcurrentDictionary<string, Camera.MotionSummary> LastMotionGif = new ConcurrentDictionary<string, Camera.MotionSummary>();

        public static readonly ConcurrentDictionary<string, System.Threading.CancellationTokenSource> Cancellations = new ConcurrentDictionary<string, CancellationTokenSource>();

        public static IConfigurationSection Settings { get; private set; }

        private Dictionary<string, List<Camera.CameraFrameEventArgs>> LastMotionFrames = new Dictionary<string, List<Camera.CameraFrameEventArgs>>();



        private readonly ILogger<Worker> _logger;
        // requires using Microsoft.Extensions.Configuration;
        private readonly IConfiguration Configuration;
        
        private Dictionary<string, Task> streamtasks = new Dictionary<string, Task>();

        private Dictionary<string, Camera.FFMpegRunner> FFMpegs = new Dictionary<string, Camera.FFMpegRunner>();
        private Dictionary<string, Task> ffmpegtasks = new Dictionary<string, Task>();

        private Dictionary<string, Camera.RtspMJpegStills> RtspMJpegStills = new Dictionary<string, Camera.RtspMJpegStills>();
        private Dictionary<string, Task> rtspmjpegstillstasks = new Dictionary<string, Task>();
        

        private EmailSettings email;
        private CloudSettings cloud;        

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            Configuration = configuration;

            Settings = Configuration.GetSection("Settings");
            var cameras = Configuration.GetSection("Cameras").GetChildren();
            CameraDetails = cameras.Select(x => new Camera.CameraDetail
            {
                Title = x["Title"],
                SourceURL = x["SourceURL"],
                StoragePath = Settings["StoragePath"],
                FFMpegRecordingSourceURL = x["FFMpegRecordingSourceURL"],
                NotifyFrames = String.IsNullOrEmpty(x["NotifyFrames"]) ? 0 : int.Parse(x["NotifyFrames"]),
                MotionInclude = Camera.CameraDetail.MakeMotionInclude(x["MotionInclude"]),
                FFMpegPath = Settings["FFMpegPath"],                
                MotionThresholdPct = double.Parse(Settings["MotionThresholdPct"]),
                MotionMinAreaPct = double.Parse(Settings["MotionMinAreaPct"]),
                MotionBackgroundAdaptSpeedPct = double.Parse(Settings["MotionBackgroundAdaptSpeedPct"])


            }).ToArray();

            email = Configuration.GetSection("EmailSettings").Get<EmailSettings>();
            cloud = Configuration.GetSection("CloudSettings").Get<CloudSettings>();
        }

        private async Task EmailNotifyAsync(int frameCount, Camera.CameraFrameEventArgs frame, CancellationToken stoppingToken)
        {
            _logger.LogInformation("Sending email motion notification for {0}", frame.Detail.Title);

            // https://www.infoworld.com/article/3534690/how-to-send-emails-in-aspnet-core.html
            var mimeMessage = new MimeMessage();
            mimeMessage.From.Add(new MailboxAddress("WideEyes Motion Detection", email.Sender));
            var recvs = email.Reciever.Split(',').ToArray();
            foreach (var recv in recvs)
                mimeMessage.To.Add(MailboxAddress.Parse(recv));

            mimeMessage.Subject = "Motion: " + frame.Detail.Title;


            
            var body = new TextPart("plain")
            {
                Text = $"Frames: {frameCount}\nMotion Time: {frame.When.ToShortDateString()} {frame.When.ToShortTimeString()}"
            };

            using (var memStream = new System.IO.MemoryStream(frame.JPEG))
            {
                // create an image attachment for the file located at path
                var attachment = new MimePart("image", "jpeg")
                {
                    Content = new MimeContent(memStream, ContentEncoding.Default),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                    ContentTransferEncoding = ContentEncoding.Base64,
                    FileName = $"motion_{frame.Detail.Title}_{frame.When.ToString("yyyy.MM.dd.HH.mm.ss")}.jpg"
                };

                // now create the multipart/mixed container to hold the message text and the
                // image attachment
                var multipart = new Multipart("mixed");
                multipart.Add(body);
                multipart.Add(attachment);

                // now set the multipart/mixed as the message body
                mimeMessage.Body = multipart;
                

                using (SmtpClient smtpClient = new SmtpClient())
                {                    
                    try
                    {
                        await smtpClient.ConnectAsync(email.SmtpServer, email.Port, true, stoppingToken);
                        await smtpClient.AuthenticateAsync(email.UserName, email.Password, stoppingToken);
                        await smtpClient.SendAsync(mimeMessage);
                        await smtpClient.DisconnectAsync(true, stoppingToken);
                    } 
                    catch (Exception e)
                    {
                        _logger.LogError("Unable to send email", e);
                    }
                }
            }

                
        }

        private void UploadFiles(IEnumerable<string> filebase)
        {

            // CANNOT use Path.Combine because on windows it make Windowsy paths :-|
            if (String.IsNullOrEmpty(cloud.Path))
                cloud.Path = ".";

            if (!cloud.Path.EndsWith("/"))
                cloud.Path += "/";

            using (var client = new SftpClient(cloud.Host, cloud.Username, cloud.Password))
            {
                client.Connect();

                foreach (var fb in filebase)
                {
                    var basefi = new FileInfo(fb);

                    var files = Directory.EnumerateFiles(basefi.DirectoryName, basefi.Name + ".*");


                    foreach (var file in files)
                    {
                        var fi = new FileInfo(file);

                        var dest = System.IO.Path.Combine(cloud.Path, fi.Name);

                        using (Stream fileStream = fi.OpenRead())
                        {
                            client.UploadFile(fileStream, dest);  
                        }
                    }
                }
                            

            }
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            

            DateTime laststep = DateTime.Now;
            List<string> filebaseToUpload = new List<string>();


            while (!stoppingToken.IsCancellationRequested)
            {
                // _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);

                foreach (var det in CameraDetails)
                {
                    if (!streamtasks.ContainsKey(det.Title))
                    {
                        streamtasks.Add(det.Title, null);
                        ffmpegtasks.Add(det.Title, null);
                        rtspmjpegstillstasks.Add(det.Title, null);
                        LastMotionFrames.Add(det.Title, new List<Camera.CameraFrameEventArgs>());                        
                    }
                    Task task;


                    // start/restart all tasks
                    CancellationToken token;
                    if (!Cancellations.ContainsKey(det.Title))
                        Cancellations[det.Title] = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    
                    token = Cancellations[det.Title].Token;


                    // start/restart ffmpeg
                    task = ffmpegtasks[det.Title];
                    if (!token.IsCancellationRequested && (task == null || task.IsCompleted))
                    {
                        // create new task          
                        _logger.LogInformation("Creating new ffmpeg for {camera} at: {time}", det.Title, DateTimeOffset.Now);
                        var str = new Camera.FFMpegRunner(det, x => _logger.LogInformation("FFmpeg {0} says {1} at {2}", det.Title, x, DateTimeOffset.Now));
                        if (FFMpegs.ContainsKey(det.Title))
                        {                            
                            FFMpegs.Remove(det.Title);
                        }

                        FFMpegs.Add(det.Title, str);
                        
                        var strtask = str.Run(token);


                        ffmpegtasks[det.Title] = strtask;
                    }
                    

                    task = rtspmjpegstillstasks[det.Title];
                    if (!token.IsCancellationRequested && (task == null || task.IsCompleted))
                    {
                        // create new task          
                        _logger.LogInformation("Creating new RtspMJpegStills for {camera} at: {time}", det.Title, DateTimeOffset.Now);
                        var str = new Camera.RtspMJpegStills(det, x => _logger.LogInformation("FFmpegStils {0} says {1} at {2}", det.Title, x, DateTimeOffset.Now));
                        if (RtspMJpegStills.ContainsKey(det.Title))
                        {
                            RtspMJpegStills.Remove(det.Title);
                        }

                        RtspMJpegStills.Add(det.Title, str);
                        str.CameraFrameEvent += Str_CameraFrameEvent;
                        var strtask = str.Run(token);


                        rtspmjpegstillstasks[det.Title] = strtask;
                    }

                    // for the rest of these we will use general stopping token
                    // instead of camera specific token
                    // since they are clean up that can still run after the camera itself is turnd off

                    // Motion: 60 sec window
                    // Take all motion older than 60 seconds 
                    // Note: previously had a roling window - that was bad
                    // need distinct "previous minute" - any different minute will do
                    // midpoint for frame   
                    // .Where(f => f.When.Minute != DateTime.Now.Minute && !f.HasBeenMotionProcessed)
                    // One concern is if most motion is the prior minute and like 1 or 2 frames
                    // in this minute
                    // Ok new idea
                    // if there is any motion in the last (rolling) 60 seconds
                    // .Where(f => DateTime.Now - f.When < TimeSpan.FromMinutes(1))
                    // switch back to the old if statement
                    // due to the running of this thing every second
                    // but we keep 2 minutes for the gif

                    if (LastMotionFrames[det.Title]
                        .Where(f => f.When.Minute != DateTime.Now.Minute && !f.HasBeenMotionProcessed)                        
                        .Any())
                    {

                        var oldMotionFrames = LastMotionFrames[det.Title]
                            .Where(f => DateTime.Now - f.When < TimeSpan.FromMinutes(2))
                            .ToArray();                        

                        foreach (var f in oldMotionFrames)
                            f.HasBeenMotionProcessed = true;

                        // keep the old motion for now
                        // we will use it for the hourly summary                    
                        
                        // the -1 is to bias toward beginnign since end is sometimes just the original frame, you know
                        // the motion of the item leaving, the original frame is now "new"
                        int midpoint = Math.Max(0, oldMotionFrames.Length / 2 - 1);
                        var motionFrame = oldMotionFrames[midpoint];                        

                        if (det.NotifyFrames > 0 && oldMotionFrames.Length >= det.NotifyFrames)
                        {
                            // lack of await is intentional
                            _ = EmailNotifyAsync(oldMotionFrames.Length, motionFrame, stoppingToken);                            
                        }

                        var frames = LastMotionFrames[det.Title].ToArray();

                        _ = Task.Run(() =>
                        {
                            
                            try
                            {
                                LastMotionGif[det.Title] = Camera.MotionSummary.RunToInMemoryGif(det, frames, stoppingToken);
                                _logger.LogInformation("Completed new motion gif for {camera} at: {time}", det.Title, DateTimeOffset.Now);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("Failed to create new motion gif: {e}", ex);
                            }

                        });

                        
                    }

                    if (LastMotionFrames[det.Title].Any() && DateTime.Now.Hour != laststep.Hour)
                    {
                        // new hour
                        // hourly motion video
                        var frames = LastMotionFrames[det.Title].ToArray();

                        _ =Task.Run(() => {
                            _logger.LogInformation("Creating new motion mp4 for {camera} at: {time}", det.Title, DateTimeOffset.Now);
                            try
                            {
                                var filebase = MotionSummary.RunToSummaryMp4(det, frames, stoppingToken);
                                _logger.LogInformation("Completed new motion mp4 for {camera} at: {time}", det.Title, DateTimeOffset.Now);
                                if (String.IsNullOrEmpty(filebase))
                                {
                                    _logger.LogWarning("No file base provided to upload new motion mp4 for {camera} at: {time}", det.Title, DateTimeOffset.Now);
                                } 
                                else
                                {
                                    filebaseToUpload.Add(filebase); 
                                }                                                                
                                

                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("Failed to create new motion mp4: {e}", ex);
                            }
                            

                        });
                        LastMotionFrames[det.Title].Clear();
                    }

                }

                // outside per-camera loop

                if (filebaseToUpload.Any())
                {
                    _ = Task.Run(() =>
                    {
                        _logger.LogInformation("Proceeding to upload new motion mp4s at: {time}", DateTimeOffset.Now);
                        try
                        {
                            // since this runs in the background, get a copy of the list now
                            // then clear the list so it can be added to now
                            var toupload = filebaseToUpload.ToArray();
                            filebaseToUpload.Clear();

                            UploadFiles(toupload); 
                            _logger.LogInformation("Completed upload new motion mp4s at: {time}", DateTimeOffset.Now);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Unable to upload motion: {ex}", ex);
                        }
                        
                    });
                }
                


                if (DateTime.Now.Day != laststep.Day)
                {
                    // end of day
                    // daily maintenance window
                    var oldrem = new OldFileRemover(_logger, Configuration);
                    _ = Task.Run(() => oldrem.Run(stoppingToken));

                    GC.Collect();

                }

                laststep = DateTime.Now;
                await Task.Delay(1000, stoppingToken);
            }
        }

        private void Str_CameraFrameEvent(object sender, Camera.CameraFrameEventArgs e)
        {
            if (e.JPEG == null)
            {
                LastFrame.Remove(e.Detail.Title, out _);
            }
            else
            {
                LastFrame[e.Detail.Title] = e;
                if (e.HasMotion)
                {
                    LastMotionFrames[e.Detail.Title].Add(e);                    
                }
                
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Disposing streams at: {time}", DateTimeOffset.Now);
            

            return base.StopAsync(cancellationToken);
        }
    }
}
