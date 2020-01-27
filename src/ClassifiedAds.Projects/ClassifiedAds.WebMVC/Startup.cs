﻿using ClassifiedAds.DomainServices.DomainEvents;
using ClassifiedAds.DomainServices.Identity;
using ClassifiedAds.DomainServices.Infrastructure.MessageBrokers;
using ClassifiedAds.DomainServices.Infrastructure.Storages;
using ClassifiedAds.Infrastructure.Identity;
using ClassifiedAds.Infrastructure.MessageBrokers.AzureQueue;
using ClassifiedAds.Infrastructure.MessageBrokers.AzureServiceBus;
using ClassifiedAds.Infrastructure.MessageBrokers.Kafka;
using ClassifiedAds.Infrastructure.MessageBrokers.RabbitMQ;
using ClassifiedAds.Infrastructure.Storages.Amazon;
using ClassifiedAds.Infrastructure.Storages.Azure;
using ClassifiedAds.Infrastructure.Storages.Local;
using ClassifiedAds.WebMVC.Authorization;
using ClassifiedAds.WebMVC.ClaimsTransformations;
using ClassifiedAds.WebMVC.ConfigurationOptions;
using ClassifiedAds.WebMVC.Filters;
using ClassifiedAds.WebMVC.HttpHandlers;
using ClassifiedAds.WebMVC.Middleware;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.IO;

namespace ClassifiedAds.WebMVC
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment env)
        {
            Configuration = configuration;

            Directory.CreateDirectory(Path.Combine(env.ContentRootPath, "logs"));
            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(env.ContentRootPath, "logs", "log.txt"),
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1))
                .CreateLogger();
        }

        public IConfiguration Configuration { get; }

        private AppSettings AppSettings { get; set; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AppSettings>, AppSettingsValidation>());

            var appSettings = new AppSettings();
            Configuration.Bind(appSettings);
            AppSettings = appSettings;

            var validationResult = appSettings.Validate();
            if (validationResult.Failed)
            {
                throw new Exception(validationResult.FailureMessage);
            }

            services.Configure<AppSettings>(Configuration);

            services.Configure<CookiePolicyOptions>(options =>
            {
                // This lambda determines whether user consent for non-essential cookies is needed for a given request.
                options.CheckConsentNeeded = context => true;
                options.MinimumSameSitePolicy = SameSiteMode.None;
            });

            services.AddControllersWithViews(setupAction =>
            {
                setupAction.Filters.Add(typeof(CustomActionFilter));
                setupAction.Filters.Add(typeof(CustomResultFilter));
                setupAction.Filters.Add(typeof(CustomAuthorizationFilter));
                setupAction.Filters.Add(typeof(CustomExceptionFilter));
            })
            .AddNewtonsoftJson();

            services.AddPersistence(appSettings.ConnectionStrings.ClassifiedAds)
                    .AddDomainServices()
                    .AddMessageHandlers();

            services.AddAuthentication(options =>
            {
                options.DefaultScheme = "Cookies";
                options.DefaultChallengeScheme = "oidc";
            })
            .AddCookie("Cookies", options =>
            {
                options.AccessDeniedPath = "/Authorization/AccessDenied";
            })
            .AddOpenIdConnect("oidc", options =>
            {
                options.SignInScheme = "Cookies";
                options.Authority = appSettings.OpenIdConnect.Authority;
                options.ClientId = appSettings.OpenIdConnect.ClientId;
                options.ResponseType = "code id_token";
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("ClassifiedAds.WebAPI");
                options.Scope.Add("offline_access");
                options.SaveTokens = true;
                options.ClientSecret = "secret";
                options.GetClaimsFromUserInfoEndpoint = true;
                options.RequireHttpsMetadata = appSettings.OpenIdConnect.RequireHttpsMetadata;
            });
            services.AddSingleton<IClaimsTransformation, CustomClaimsTransformation>();

            services.AddAuthorization(options =>
            {
                options.AddPolicy("CustomPolicy", policy =>
                {
                    policy.AddRequirements(new CustomRequirement());
                });
            })
            .AddSingleton<IAuthorizationHandler, CustomRequirementHandler>();

            services.AddTransient<ProfilingHttpHandler>();
            services.AddHttpClient(string.Empty)
                    .AddHttpMessageHandler<ProfilingHttpHandler>();

            services.AddMiniProfiler(options =>
            {
                options.RouteBasePath = "/profiler"; // access /profiler/results to see last profile check
                options.PopupRenderPosition = StackExchange.Profiling.RenderPosition.BottomLeft;
                options.PopupShowTimeWithChildren = true;
            })
            .AddEntityFramework();

            services.AddHealthChecks()
                .AddSqlServer(connectionString: appSettings.ConnectionStrings.ClassifiedAds,
                    healthQuery: "SELECT 1;",
                    name: "Sql Server",
                    failureStatus: HealthStatus.Degraded)
                .AddUrlGroup(new Uri(appSettings.OpenIdConnect.Authority),
                    name: "Identity Server",
                    failureStatus: HealthStatus.Degraded)
                .AddUrlGroup(new Uri(appSettings.ResourceServer.Endpoint),
                    name: "Resource (Web API) Server",
                    failureStatus: HealthStatus.Degraded)
                .AddSignalRHub(appSettings.NotificationServer.Endpoint + "/HealthCheckHub",
                    name: "Notification (SignalR) Server",
                    failureStatus: HealthStatus.Degraded);

            services.AddHealthChecksUI(setupSettings: setup =>
            {
                setup.AddHealthCheckEndpoint("Basic Health Check", $"{appSettings.CurrentUrl}/healthcheck");
            });

            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddScoped<ICurrentUser, CurrentWebUser>();

            if (appSettings.Storage.UsedAzure())
            {
                services.AddSingleton<IFileStorageManager>(new AzureBlobStorageManager(appSettings.Storage.Azure.ConnectionString, appSettings.Storage.Azure.Container));
            }
            else if (appSettings.Storage.UsedAmazon())
            {
                services.AddSingleton<IFileStorageManager>(new AmazonS3StorageManager(appSettings.Storage.Amazon.AccessKeyID, appSettings.Storage.Amazon.SecretAccessKey, appSettings.Storage.Amazon.BucketName, appSettings.Storage.Amazon.RegionEndpoint));
            }
            else
            {
                services.AddSingleton<IFileStorageManager>(new LocalFileStorageManager(appSettings.Storage.Local.Path));
            }

            if (appSettings.MessageBroker.UsedRabbitMQ())
            {
                services.AddSingleton<IMessageSender<FileUploadedEvent>>(new RabbitMQSender<FileUploadedEvent>(new RabbitMQSenderOptions
                {
                    HostName = appSettings.MessageBroker.RabbitMQ.HostName,
                    UserName = appSettings.MessageBroker.RabbitMQ.UserName,
                    Password = appSettings.MessageBroker.RabbitMQ.Password,
                    ExchangeName = appSettings.MessageBroker.RabbitMQ.ExchangeName,
                    RoutingKey = appSettings.MessageBroker.RabbitMQ.RoutingKey_FileUploaded,
                }));

                services.AddSingleton<IMessageSender<FileDeletedEvent>>(new RabbitMQSender<FileDeletedEvent>(new RabbitMQSenderOptions
                {
                    HostName = appSettings.MessageBroker.RabbitMQ.HostName,
                    UserName = appSettings.MessageBroker.RabbitMQ.UserName,
                    Password = appSettings.MessageBroker.RabbitMQ.Password,
                    ExchangeName = appSettings.MessageBroker.RabbitMQ.ExchangeName,
                    RoutingKey = appSettings.MessageBroker.RabbitMQ.RoutingKey_FileDeleted,
                }));
            }
            else if (appSettings.MessageBroker.UsedKafka())
            {
                services.AddSingleton<IMessageSender<FileUploadedEvent>>(new KafkaSender<FileUploadedEvent>(
                    appSettings.MessageBroker.Kafka.BootstrapServers,
                    appSettings.MessageBroker.Kafka.Topic_FileUploaded));

                services.AddSingleton<IMessageSender<FileDeletedEvent>>(new KafkaSender<FileDeletedEvent>(
                    appSettings.MessageBroker.Kafka.BootstrapServers,
                    appSettings.MessageBroker.Kafka.Topic_FileDeleted));
            }
            else if (appSettings.MessageBroker.UsedAzureQueue())
            {
                services.AddSingleton<IMessageSender<FileUploadedEvent>>(new AzureQueueSender<FileUploadedEvent>(
                    connectionString: appSettings.MessageBroker.AzureQueue.ConnectionString,
                    queueName: appSettings.MessageBroker.AzureQueue.QueueName_FileUploaded));

                services.AddSingleton<IMessageSender<FileDeletedEvent>>(new AzureQueueSender<FileDeletedEvent>(
                    connectionString: appSettings.MessageBroker.AzureQueue.ConnectionString,
                    queueName: appSettings.MessageBroker.AzureQueue.QueueName_FileDeleted));
            }
            else if (appSettings.MessageBroker.UsedAzureServiceBus())
            {
                services.AddSingleton<IMessageSender<FileUploadedEvent>>(new AzureServiceBusSender<FileUploadedEvent>(
                    connectionString: appSettings.MessageBroker.AzureServiceBus.ConnectionString,
                    queueName: appSettings.MessageBroker.AzureServiceBus.QueueName_FileUploaded));

                services.AddSingleton<IMessageSender<FileDeletedEvent>>(new AzureServiceBusSender<FileDeletedEvent>(
                    connectionString: appSettings.MessageBroker.AzureServiceBus.ConnectionString,
                    queueName: appSettings.MessageBroker.AzureServiceBus.QueueName_FileDeleted));
            }
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");

                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseSecurityHeaders();

            app.UseIPFiltering();

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseCookiePolicy();

            app.UseMiniProfiler();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseHealthChecks("/healthcheck", new HealthCheckOptions
            {
                Predicate = _ => true,
                ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
                ResultStatusCodes =
                {
                    [HealthStatus.Healthy] = StatusCodes.Status200OK,
                    [HealthStatus.Degraded] = StatusCodes.Status500InternalServerError,
                    [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
                },
            });
            app.UseHealthChecksUI(); // /healthchecks-ui#/healthchecks

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapDefaultControllerRoute();
            });

            try
            {
                TestMessageBrokerReceivers();
            }
            catch
            {
            }
        }

        private void TestMessageBrokerReceivers()
        {
            IMessageReceiver<FileUploadedEvent> fileUploadedMessageQueueReceiver = null;
            IMessageReceiver<FileDeletedEvent> fileDeletedMessageQueueReceiver = null;

            if (AppSettings.MessageBroker.UsedRabbitMQ())
            {
                fileUploadedMessageQueueReceiver = new RabbitMQReceiver<FileUploadedEvent>(new RabbitMQReceiverOptions
                {
                    HostName = AppSettings.MessageBroker.RabbitMQ.HostName,
                    UserName = AppSettings.MessageBroker.RabbitMQ.UserName,
                    Password = AppSettings.MessageBroker.RabbitMQ.Password,
                    QueueName = AppSettings.MessageBroker.RabbitMQ.QueueName_FileUploaded,
                });

                fileDeletedMessageQueueReceiver = new RabbitMQReceiver<FileDeletedEvent>(new RabbitMQReceiverOptions
                {
                    HostName = AppSettings.MessageBroker.RabbitMQ.HostName,
                    UserName = AppSettings.MessageBroker.RabbitMQ.UserName,
                    Password = AppSettings.MessageBroker.RabbitMQ.Password,
                    QueueName = AppSettings.MessageBroker.RabbitMQ.QueueName_FileDeleted,
                });
            }

            if (AppSettings.MessageBroker.UsedKafka())
            {
                fileUploadedMessageQueueReceiver = new KafkaReceiver<FileUploadedEvent>(
                    AppSettings.MessageBroker.Kafka.BootstrapServers,
                    AppSettings.MessageBroker.Kafka.Topic_FileUploaded,
                    AppSettings.MessageBroker.Kafka.GroupId);

                fileDeletedMessageQueueReceiver = new KafkaReceiver<FileDeletedEvent>(
                    AppSettings.MessageBroker.Kafka.BootstrapServers,
                    AppSettings.MessageBroker.Kafka.Topic_FileDeleted,
                    AppSettings.MessageBroker.Kafka.GroupId);
            }

            if (AppSettings.MessageBroker.UsedAzureQueue())
            {
                fileUploadedMessageQueueReceiver = new AzureQueueReceiver<FileUploadedEvent>(
                    AppSettings.MessageBroker.AzureQueue.ConnectionString,
                    AppSettings.MessageBroker.AzureQueue.QueueName_FileUploaded);

                fileDeletedMessageQueueReceiver = new AzureQueueReceiver<FileDeletedEvent>(
                    AppSettings.MessageBroker.AzureQueue.ConnectionString,
                    AppSettings.MessageBroker.AzureQueue.QueueName_FileDeleted);
            }

            if (AppSettings.MessageBroker.UsedAzureServiceBus())
            {
                fileUploadedMessageQueueReceiver = new AzureServiceBusReceiver<FileUploadedEvent>(
                    AppSettings.MessageBroker.AzureServiceBus.ConnectionString,
                    AppSettings.MessageBroker.AzureServiceBus.QueueName_FileUploaded);

                fileDeletedMessageQueueReceiver = new AzureServiceBusReceiver<FileDeletedEvent>(
                    AppSettings.MessageBroker.AzureServiceBus.ConnectionString,
                    AppSettings.MessageBroker.AzureServiceBus.QueueName_FileDeleted);
            }

            var connection = new HubConnectionBuilder()
                .WithUrl($"{AppSettings.NotificationServer.Endpoint}/SimulatedLongRunningTaskHub")
                .AddMessagePackProtocol()
                .Build();

            fileUploadedMessageQueueReceiver?.Receive(data =>
            {
                string message = data.FileEntry.Id.ToString();

                connection.StartAsync().GetAwaiter().GetResult();

                connection.InvokeAsync("SendTaskStatus", $"{AppSettings.MessageBroker.Provider} - File Uploaded", message);

                connection.StopAsync().GetAwaiter().GetResult();
            });

            fileDeletedMessageQueueReceiver?.Receive(data =>
            {
                string message = data.FileEntry.Id.ToString();

                connection.StartAsync().GetAwaiter().GetResult();

                connection.InvokeAsync("SendTaskStatus", $"{AppSettings.MessageBroker.Provider} - File Deleted", message);

                connection.StopAsync().GetAwaiter().GetResult();
            });
        }
    }
}
