using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Asn1;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CameraService
{
    public class OldFileRemover
    {
        private readonly ILogger<Worker> _logger;
        private string StoragePath;
        private int RetentionDays;

        public OldFileRemover(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;

            var settings = configuration.GetSection("Settings");
            StoragePath = settings["StoragePath"];
            RetentionDays = int.Parse(settings["RetentionDays"]);
        }

        private int RunForDirectory(string dirName, CancellationToken stoppingToken)
        {

            string[] files = Directory.GetFiles(dirName);
            int count = 0;

            // delete old files here
            foreach (string file in files)
            {
                if (stoppingToken.IsCancellationRequested)
                    return count;

                FileInfo fi = new FileInfo(file);
                if (fi.CreationTime < DateTime.Now.AddDays(-1 * RetentionDays))
                {
                    fi.Delete();
                    count++;
                }
            }

            // find subdirs
            string[] subdirs = Directory.GetDirectories(dirName);
            foreach (string subdir in subdirs)
            {
                if (stoppingToken.IsCancellationRequested)
                    return count;

                count += RunForDirectory(subdir, stoppingToken);

                // if that dir is now empty, remove it
                if (!Directory.EnumerateFileSystemEntries(subdir).Any())
                {
                    Directory.Delete(subdir);
                    count++;
                }


            }

            return count;
        }

        public void Run(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Beginning old file removal at: {time}", DateTimeOffset.Now); 
            try 
            {
                var count = RunForDirectory(StoragePath, stoppingToken);
                _logger.LogInformation("Completed removing {0} old files and directories at: {time}", count, DateTimeOffset.Now);
            } 
            catch (Exception e)
            {
                _logger.LogWarning("Exception/failed to remove files and directories: {e}", e);
            }
        }

    }
}
