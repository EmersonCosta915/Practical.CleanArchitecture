﻿namespace ClassifiedAds.WebAPI.ConfigurationOptions.Storage
{
    public class AmazonOptions
    {
        public string AccessKeyID { get; set; }

        public string SecretAccessKey { get; set; }

        public string BucketName { get; set; }

        public string RegionEndpoint { get; set; }
    }
}
