using System;
using System.Collections.Generic;

namespace FAVIS
{
    public static class GrabberFactory
    {
        public static IGrabber Create(IReadOnlyList<GrabberConfig> configurations, string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) == false)
            {
                return new FileGrabber(imagePath);
            }

            IReadOnlyList<GrabberConfig> grabberConfigurations = configurations ?? new[] { CreateDefaultGrabberConfiguration() };
            return new CompositeGrabber(grabberConfigurations);
        }

        public static GrabberConfig CreateDefaultGrabberConfiguration(int id = 1)
        {
            return new GrabberConfig
            {
                Id = id,
                Type = GrabberType.Deediim,
                Name = $"Grabber {id}",
                IpAddress = "127.0.0.1",
                Port = 5000,
                SensorIndex = 0,
                PatternIndex = 0
            };
        }
    }
}
