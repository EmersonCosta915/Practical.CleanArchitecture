﻿using ClassifiedAds.Infrastructure.Configuration;
using ClassifiedAds.Infrastructure.HealthChecks;
using ClassifiedAds.Infrastructure.Logging;
using ClassifiedAds.WebMVC.ConfigurationOptions;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ClassifiedAds.WebMVC
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateWebHostBuilder(args).Build().Run();
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
                .UseStartup<Startup>()
                .ConfigureAppConfiguration((ctx, builder) =>
                {
                    var config = builder.Build();
                    var appSettings = new AppSettings();
                    config.Bind(appSettings);

                    if (appSettings.CheckDependency.Enabled)
                    {
                        NetworkPortCheck.Wait(appSettings.CheckDependency.Host, 5);
                    }

                    if (appSettings?.ConfigurationSources?.SqlServer?.IsEnabled ?? false)
                    {
                        builder.AddSqlConfigurationVariables(appSettings.ConfigurationSources.SqlServer);
                    }

                    if (ctx.HostingEnvironment.IsDevelopment())
                    {
                        return;
                    }

                    if (appSettings?.ConfigurationSources?.AzureKeyVault?.IsEnabled ?? false)
                    {
                        builder.AddAzureKeyVault(appSettings.ConfigurationSources.AzureKeyVault.VaultName);
                    }
                })
                .UseClassifiedAdsLogger(configuration =>
                {
                    var appSettings = new AppSettings();
                    configuration.Bind(appSettings);
                    return appSettings.Logging;
                });
    }
}
