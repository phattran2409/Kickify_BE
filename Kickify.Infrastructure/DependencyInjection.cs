using BrewView.Infrastructure.Authentication;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Kickify.Application.Abstractions.Authentication;
using Kickify.Application.Abstractions.Jobs;
using Kickify.Application.Abstractions.OTP;
using Kickify.Application.Abstractions.Persistence;
using Kickify.Application.Abstractions.Repositories;
using Kickify.Application.Abstractions.Services;
using Kickify.Infrastructure.Authentication;
using Kickify.Infrastructure.Database;
using Kickify.Infrastructure.Jobs;
using Kickify.Infrastructure.Mail;
using Kickify.Infrastructure.Payment;
using Kickify.Infrastructure.Persistence;
using Kickify.Infrastructure.Redis;
using Kickify.Infrastructure.Repositories;
using Kickify.Infrastructure.Services;
using Kickify.Infrastructure.Settings;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Minio;
using StackExchange.Redis;
using System.Text;
using Kickify.Infrastructure.ChatConnection;
using VNPAY.Extensions;
using Hangfire;
using Hangfire.PostgreSql;
using Amazon;
using Amazon.RDS.Util;

namespace Kickify.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration) =>
        services
            .AddAuthenticationInternal(configuration)
            .AddDatabase(configuration)
            .AddService(configuration)
            .AddRepository()
            .AddFirebase()
            .AddRedisStore(configuration)
            .AddMinioStorage(configuration)
            .AddSignalRServices()
            .AddVNPay(configuration)
            .AddHangfireServices(configuration)
            .AddBackgroundJobs();

        private static IServiceCollection AddAuthenticationInternal(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(o =>
                {
                    o.RequireHttpsMetadata = false;
                    o.TokenValidationParameters = new TokenValidationParameters()
                    {
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["Authentication:SecretKey"]!)),
                        ValidIssuer = configuration["Authentication:Issuer"],
                        ValidAudience = configuration["Authentication:Audience"],
                        ClockSkew = TimeSpan.Zero
                    };

                    o.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];
                            var path = context.HttpContext.Request.Path;
                            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                            {
                                context.Token = accessToken;
                            }
                            return Task.CompletedTask;
                        }
                    };
                });

            services.AddHttpContextAccessor();

            return services;
        }

        private static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                var connectionString = configuration.GetConnectionString("Database");
                bool isAuthIAM_AWS = !connectionString.Contains("Password=", StringComparison.OrdinalIgnoreCase);
                if (isAuthIAM_AWS)
                {
                    var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
                    dataSourceBuilder.UsePeriodicPasswordProvider(
                        async (constringBuilder , CancellationToken) =>
                        {
                            var regionName = configuration["AWS:Region"] ?? "ap-southeast-1";
                            var region = RegionEndpoint.GetBySystemName(regionName);
                            string token =  RDSAuthTokenGenerator.GenerateAuthToken(
                                        RegionEndpoint.APSoutheast1,
                                        constringBuilder.Host,
                                        constringBuilder.Port,
                                        constringBuilder.Username
                                );
                            return token;
                        },
                        TimeSpan.FromMinutes(10), // Tự động làm mới mật khẩu mỗi 10 phút
                        TimeSpan.FromSeconds(10)
                    );
                    var dataSource = dataSourceBuilder.Build();
                    // 3. Đăng ký DataSource vào DI Container
                    services.AddSingleton(dataSource);
                    // 4. Cấu hình DbContext sử dụng DataSource động
                    services.AddDbContext<ApplicationDbContext>(options =>
                    {
                        options.UseNpgsql(dataSource, npgsqlOptions =>
                        {
                            npgsqlOptions.EnableRetryOnFailure(
                                maxRetryCount: 5,
                                maxRetryDelay: TimeSpan.FromSeconds(10),
                                errorCodesToAdd: null);
                            npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", Schemas.System);
                        });
                    });
                }
                else
                {
                    options.UseNpgsql(connectionString, npgsqlOptions =>
                    {
                        npgsqlOptions.EnableRetryOnFailure(
                            maxRetryCount: 5,
                            maxRetryDelay: TimeSpan.FromSeconds(10),
                            errorCodesToAdd: null);

                        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", Schemas.System);
                    });
                }
               
            });
            services.AddScoped<IApplicationDbContext>(provider =>
                provider.GetRequiredService<ApplicationDbContext>());

            return services;
        }
        private static IServiceCollection AddService(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<EmailSettings>(configuration.GetSection("Email"));
            services.AddScoped<IAuthenticationServices, AuthenticationServices>();
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<IUserContext, UserContext>();
            services.AddScoped<ICurrentUserReader, CurrentUserReader>();
            services.AddScoped<IJwtProvider, JwtProvider>();
            services.AddScoped<IPasswordHasher, PasswordHasher>();
            services.AddScoped<EmailTemplateService>();
            services.AddScoped<IMailService, MailService>();
            services.AddScoped<IOtpGenerator, OtpGenerator>();
            services.AddScoped<IRedisOtpStore, RedisOtpStore>();
            services.AddTransient<IResetPasswordGenerator, ResetPasswordGenerator>();
            services.AddScoped<IPushNotificationService, PushNotificationService>();
            services.AddSingleton<IQrCodeService, QrCodeService>();
            services.AddScoped<ILeaderboardCacheService, LeaderboardCacheService>();
            services.AddScoped<ITrustScoreService, TrustScoreService>();
            services.AddSingleton<SystemLogQueue>();
            services.AddSingleton<ISystemLogQueue>(sp => sp.GetRequiredService<SystemLogQueue>());

            // AI Sentiment Analysis Service
            services.AddHttpClient<ISentimentAnalysisService, SentimentAnalysisService>((sp, client) =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var baseUrl = config["SentimentAnalysis:BaseUrl"] ?? "http://localhost:8000";
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
            });

            // AI Suggestion Service (shared base URL with sentiment analysis)
            services.AddHttpClient<IAiSuggestionService, AiSuggestionService>((sp, client) =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var baseUrl = config["SentimentAnalysis:BaseUrl"] ?? "http://localhost:8000";
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
            });

            return services;
        }
        private static IServiceCollection AddRepository(this IServiceCollection services)
        {
            services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
            services.AddScoped<IPostRepository, PostRepository>();
            services.AddScoped<IPaymentRequestRepository, PaymentRequestRepository>();
            services.AddScoped<IPlayerProfileRepository, PlayerProfileRepository>();
            services.AddScoped<IWalletRepository, WalletRepository>();
            services.AddScoped<IWalletTransactionRepository, WalletTransactionRepository>();
            services.AddScoped<IWalletWithdrawalRepository, WalletWithdrawalRepository>();
            services.AddScoped<IVenueRepository, VenueRepository>();
            services.AddScoped<IHolidayRepository, HolidayRepository>();
            services.AddScoped<IVenuePhotoRepository, VenuePhotoRepository>();
            services.AddScoped<IVenueOperatingHourRepository, VenueOperatingHourRepository>();
            services.AddScoped<IFieldRepository, FieldRepository>();
            services.AddScoped<IBookingRepository, BookingRepository>();
            services.AddScoped<IMatchRoomRepository, MatchRoomRepository>();
            services.AddScoped<IMatchPresetRepository, MatchPresetRepository>();
            services.AddScoped<IMatchFeedbackRepository, MatchFeedbackRepository>();
            services.AddScoped<IRoomParticipantRepository, RoomParticipantRepository>();
            services.AddScoped<IPostLikeRepository, PostLikeRepository>();
            services.AddScoped<IChatMessageRepository, ChatMessageRepository>();
            services.AddScoped<ICommentRepository, CommentRepository>();
            services.AddScoped<ICommentLikeRepository, CommentLikeRepository>();
            services.AddScoped<IFriendshipRepository, FriendshipRepository>();
            services.AddScoped<IMatchFormationRepository, MatchFormationRepository>();
            services.AddScoped<IFormationAssignmentRepository, FormationAssignmentRepository>();
            services.AddScoped<IMatchResultVoteRepository, MatchResultVoteRepository>();
            services.AddScoped<INotificationRepository, NotificationRepository>();
            services.AddScoped<INotificationPreferenceRepository, NotificationPreferenceRepository>();
            services.AddScoped<IRoomInvitationRepository, RoomInvitationRepository>();
            services.AddScoped<IAchievementRepository, AchievementRepository>();
            services.AddScoped<IVenueReviewRepository, VenueReviewRepository>();
            services.AddScoped<IPlayerReportRepository, PlayerReportRepository>();
            services.AddScoped<IContentReportRepository, ContentReportRepository>();
            services.AddScoped<IAnnouncementRepository, AnnouncementRepository>();
            services.AddScoped<IVenueEvidenceRepository, VenueEvidenceRepository>();
            return services;
        }
        private static IServiceCollection AddFirebase(this IServiceCollection services)
        {
            FirebaseApp.Create(new AppOptions
            {
                Credential = GoogleCredential.FromFile("firebase.json"),
            });
            return services;
        }
        private static IServiceCollection AddRedisStore(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(configuration.GetConnectionString("Redis")))
                    .AddTransient<IRedisOtpStore, RedisOtpStore>();
            return services;
        }
        private static IServiceCollection AddMinioStorage(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<MinioSettings>(configuration.GetSection(MinioSettings.SectionName));

            var minioSettings = configuration.GetSection(MinioSettings.SectionName).Get<MinioSettings>()!;

            services.AddSingleton<IMinioClient>(_ =>
            {
                var client = new MinioClient()
                    .WithEndpoint(minioSettings.Endpoint)
                    .WithCredentials(minioSettings.AccessKey, minioSettings.SecretKey);

                if (minioSettings.UseSSL)
                {
                    client.WithSSL();
                }

                return client.Build();
            });

            services.AddScoped<IStorageService, MinioStorageService>();

            return services;
        }

        private static IServiceCollection AddSignalRServices(this IServiceCollection services)
        {
            services.AddSignalR();
            services.AddSingleton<ConnectionMapping>();
            return services;
        }

        private static IServiceCollection AddVNPay(this IServiceCollection services, IConfiguration configuration)
        {
            var vnpayConfig = configuration.GetSection("VNPAY");
            services.AddVnpayClient(config =>
            {
                config.TmnCode = vnpayConfig["TmnCode"]!;
                config.HashSecret = vnpayConfig["HashSecret"]!;
                config.BaseUrl = vnpayConfig["BaseUrl"]!;
                config.CallbackUrl = vnpayConfig["CallbackUrl"]!;
                config.Version = vnpayConfig["Version"]!;
                config.OrderType = vnpayConfig["OrderType"]!;
            });

            services.AddScoped<IVnPayService, VnPayService>();

            return services;
        }

        private static IServiceCollection AddHangfireServices(this IServiceCollection services, IConfiguration configuration)
        {
            var connectionString = configuration.GetConnectionString("Database");

            services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage(options =>
                    options.UseNpgsqlConnection(connectionString),
                    new PostgreSqlStorageOptions
                    {
                        PrepareSchemaIfNecessary = true,
                        SchemaName = "hangfire",
                        QueuePollInterval = TimeSpan.FromSeconds(15)
                    }));

            services.AddHangfireServer();

            return services;
        }

        private static IServiceCollection AddBackgroundJobs(this IServiceCollection services)
        {
            services.AddScoped<IEmailJobService, EmailJobService>();
            services.AddScoped<IRoomAutoCloseService, RoomAutoCloseService>();
            services.AddScoped<IMatchLifecycleService, MatchLifecycleService>();
            services.AddScoped<ILeaderboardUpdateService, LeaderboardUpdateService>();
            services.AddScoped<ISystemLogCleanupService, SystemLogCleanupService>();
            services.AddHostedService<JobSchedulerStartupService>();
            services.AddHostedService<SystemLogBatchInsertService>();

            return services;
        }
    }
}
