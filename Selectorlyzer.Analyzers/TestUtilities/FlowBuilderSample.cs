using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Selectorlyzer.Qulaly.Matcher;

namespace Selectorlyzer.TestUtilities;

public static class FlowBuilderSample
{
    public static (Compilation Compilation, CompilationUnitSyntax Root) BuildSample()
        {
            var code = @"using System;

namespace Sample
{
    public abstract class ControllerBase { }

    public interface IMediator
    {
        TResponse Send<TResponse>(IRequest<TResponse> request);
    }

    public interface IRequest<T> { }

    public sealed class GetUserQuery : IRequest<UserDto> { }

    public sealed class UserDto { }

    public interface IUserService
    {
        UserDto GetUser(string id);
    }

    public interface IUserRepository
    {
        void Add(UserDto dto);
        UserDto Find(string id);
    }

    public interface ILogger<T>
    {
        void Log(string message);
    }

    public sealed class HttpClient
    {
        public string Get(string route) => route;
    }

    public interface IOptions<T>
    {
        T Value { get; }
    }

    public sealed class UserSettings
    {
        public string Endpoint { get; set; } = string.Empty;
    }

    [HttpController]
    public sealed class UserController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly IUserService _userService;
        private readonly IUserRepository _repository;
        private readonly ILogger<UserController> _logger;
        private readonly HttpClient _client;
        private readonly IOptions<UserSettings> _settings;

        public UserController(IMediator mediator, IUserService userService, IUserRepository repository, ILogger<UserController> logger, HttpClient client, IOptions<UserSettings> settings)
        {
            _mediator = mediator;
            _userService = userService;
            _repository = repository;
            _logger = logger;
            _client = client;
            _settings = settings;
        }

        [HttpGet(""/users"")]
        public UserDto GetUser(string id)
        {
            _logger.Log(""Fetching"");
            var dto = _mediator.Send(new GetUserQuery());
            var serviceResult = _userService.GetUser(id);
            _repository.Add(serviceResult);
            var remote = _client.Get(""/remote"");
            var endpoint = _settings.Value.Endpoint;
            return serviceResult;
        }
    }

    public sealed class HttpControllerAttribute : Attribute { }

    public sealed class HttpGetAttribute : Attribute
    {
        public HttpGetAttribute(string template) { }
    }

    public interface IServiceCollection { }

    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services) => services;
    }

    public static class CompositionRoot
    {
        public static void Configure(IServiceCollection services)
        {
            services.AddScoped<IUserService, UserService>();
        }
    }

    public sealed class UserService : IUserService
    {
        public UserDto GetUser(string id) => new UserDto();
    }
}
";

            var syntaxTree = CSharpSyntaxTree.ParseText(code);
            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            };

            var compilation = CSharpCompilation.Create("FlowSample", new[] { syntaxTree }, references);
            var root = syntaxTree.GetCompilationUnitRoot();
            return (compilation, root);
        }


    public static (Compilation Compilation, CompilationUnitSyntax[] Roots, SelectorQueryContext QueryContext) BuildComprehensiveSample()
        {
            var files = new Dictionary<string, string>
            {
                ["Common/Common.cs"] = """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sample;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RouteAttribute : Attribute
{
    public RouteAttribute(string template) => Template = template;
    public string Template { get; }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class HttpControllerAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public sealed class HttpGetAttribute : Attribute
{
    public HttpGetAttribute(string template) => Template = template;
    public string Template { get; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ProducesResponseTypeAttribute : Attribute
{
    public ProducesResponseTypeAttribute(int statusCode) => StatusCode = statusCode;
    public int StatusCode { get; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AuthorizeAttribute : Attribute
{
    public string? Policy { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowAnonymousAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class)]
public sealed class TableAttribute : Attribute
{
    public TableAttribute(string name) => Name = name;
    public string Name { get; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class KeyAttribute : Attribute { }

public abstract class ControllerBase { }

public interface IRequest<TResponse> { }

public interface IRequestHandler<TRequest, TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

public interface INotification { }

public interface INotificationHandler<TNotification>
{
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}

public abstract class AbstractValidator<T>
{
    protected void RuleFor<TProperty>(Func<T, TProperty> accessor) { }
}

public interface IPipelineBehavior<TRequest, TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken, RequestHandlerDelegate<TResponse> next);
}

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

public interface IMediator
{
    Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request);
}

public abstract class BackgroundService
{
    protected abstract Task ExecuteAsync(CancellationToken stoppingToken);
}

public interface IServiceCollection
{
    IServiceCollection AddScoped<TService, TImplementation>();
    IServiceCollection AddSingleton<TService, TImplementation>();
    IServiceCollection AddHostedService<TService>() where TService : class;
    IServiceCollection AddHttpClient(string name);
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMediatR(this IServiceCollection services) => services;
}

public sealed class Guard
{
    public static Guard Against { get; } = new Guard();
    public void Null(object value, string name) { }
}

public class DbContext
{
    public DbSet<TEntity> Set<TEntity>() where TEntity : class => new DbSet<TEntity>();
}

public class DbSet<TEntity> where TEntity : class
{
    public void Add(TEntity entity) { }
    public TEntity? Find(object key) => default;
}

public interface ILogger<T>
{
    void LogInformation(string message);
}

public interface IMemoryCache
{
    bool TryGetValue(object key, out object value);
    void Set(object key, object value);
}

public interface IMapper
{
    TDestination Map<TDestination>(object source);
}

public interface IHttpClientFactory
{
    HttpClient CreateClient(string name);
}

public sealed class HttpClient
{
    public string GetString(string url) => url;
    public Task<string> GetStringAsync(string url) => Task.FromResult(url);
}

public interface IOptions<T>
{
    T Value { get; }
}

public sealed class ServiceBusClient
{
    public ServiceBusSender CreateSender(string name) => new ServiceBusSender(name);
}

public sealed class ServiceBusSender
{
    public ServiceBusSender(string name) => Name = name;
    public string Name { get; }
}

public sealed class ServiceBusMessage
{
    public string Subject { get; set; } = string.Empty;
}

public interface ICache
{
    object? Get(string key);
    void Set(string key, object value);
}

public sealed class WebApplication
{
    public string MapGet(string template, Func<string> handler) => template;
}
""",
                ["Configuration/UserSettings.cs"] = """
namespace Sample.Configuration;

public sealed class UserSettings
{
    public const string SectionName = "UserSettings";
    public string Endpoint { get; set; } = "https://api.sample.local";
    public int Timeout { get; set; } = 30;
}
""",
                ["Contracts/Models.cs"] = """
using System;

namespace Sample.Contracts;

public sealed record UserDto(Guid Id, string Name);

public sealed class UserCreated : Sample.INotification
{
    public Guid Id { get; init; }
}
""",
                ["Data/Entities.cs"] = """
using System;
using Sample;

namespace Sample.Data;

[Table("users")]
public sealed class UserEntity
{
    [Key]
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public interface IUserRepository
{
    void Add(UserEntity entity);
    System.Threading.Tasks.Task<UserEntity?> FindAsync(string id);
}

public sealed class UserRepository : IUserRepository
{
    private readonly Sample.DbContext _db;

    public UserRepository(Sample.DbContext db) => _db = db;

    public void Add(UserEntity entity) => _db.Set<UserEntity>().Add(entity);

    public System.Threading.Tasks.Task<UserEntity?> FindAsync(string id)
        => System.Threading.Tasks.Task.FromResult(_db.Set<UserEntity>().Find(id));
}

public sealed class AppDbContext : Sample.DbContext
{
    public Sample.DbSet<UserEntity> Users => Set<UserEntity>();
}
""",
                ["Services/ServiceLayer.cs"] = """
using System.Threading;
using System.Threading.Tasks;
using Sample;
using Sample.Configuration;
using Sample.Contracts;
using Sample.Data;

namespace Sample.Services;

public interface IUserService
{
    Task<UserDto> GetUserAsync(string id);
}

public sealed class UserService : IUserService
{
    private readonly IUserRepository _repository;
    private readonly ILogger<UserService> _logger;
    private readonly IMapper _mapper;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _clientFactory;

    public UserService(IUserRepository repository, ILogger<UserService> logger, IMapper mapper, IMemoryCache cache, IHttpClientFactory clientFactory)
    {
        _repository = repository;
        _logger = logger;
        _mapper = mapper;
        _cache = cache;
        _clientFactory = clientFactory;
    }

    public async Task<UserDto> GetUserAsync(string id)
    {
        Guard.Against.Null(id, nameof(id));
        if (!_cache.TryGetValue(id, out var cached))
        {
            var entity = await _repository.FindAsync(id);
            var dto = _mapper.Map<UserDto>(entity!);
            _cache.Set(id, dto);
            return dto;
        }

        _logger.LogInformation("Returning cached");
        return (UserDto)cached!;
    }
}

public sealed class UserWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }
}

public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger) => _logger = logger;

    public async Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken, RequestHandlerDelegate<TResponse> next)
    {
        _logger.LogInformation("Handling");
        return await next();
    }
}

public sealed class UserNotificationHandler : INotificationHandler<Sample.Contracts.UserCreated>
{
    private readonly Sample.Messaging.UserPublisher _publisher;

    public UserNotificationHandler(Sample.Messaging.UserPublisher publisher) => _publisher = publisher;

    public Task Handle(Sample.Contracts.UserCreated notification, CancellationToken cancellationToken)
    {
        _publisher.Publish(notification);
        return Task.CompletedTask;
    }
}
""",
                ["Messaging/Messaging.cs"] = """
using System.Threading.Tasks;
using Sample;
using Sample.Contracts;

namespace Sample.Messaging;

public sealed class GetUserQuery : IRequest<UserDto>
{
    public GetUserQuery(string id) => Id = id;
    public string Id { get; }
}

public sealed class GetUserQueryHandler : IRequestHandler<GetUserQuery, UserDto>
{
    private readonly Sample.Services.IUserService _service;

    public GetUserQueryHandler(Sample.Services.IUserService service) => _service = service;

    public async Task<UserDto> Handle(GetUserQuery request, System.Threading.CancellationToken cancellationToken)
        => await _service.GetUserAsync(request.Id);
}

public sealed class UserPublisher
{
    private readonly ServiceBusClient _client;

    public UserPublisher(ServiceBusClient client) => _client = client;

    public void Publish(UserCreated message)
    {
        var sender = _client.CreateSender("users");
        var payload = new ServiceBusMessage
        {
            Subject = "user.created"
        };
    }
}
""",
                ["Controllers/UserController.cs"] = """
using System.Threading.Tasks;
using Sample;
using Sample.Configuration;
using Sample.Contracts;
using Sample.Data;
using Sample.Messaging;
using Sample.Services;

namespace Sample.Controllers;

[HttpController]
[Route("api/users")]
[Authorize(Policy = "Admins")]
public sealed class UserController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserService _service;
    private readonly IUserRepository _repository;
    private readonly ILogger<UserController> _logger;
    private readonly HttpClient _client;
    private readonly IOptions<UserSettings> _settings;
    private readonly IMemoryCache _cache;

    public UserController(IMediator mediator, IUserService service, IUserRepository repository, ILogger<UserController> logger, HttpClient client, IOptions<UserSettings> settings, IMemoryCache cache)
    {
        _mediator = mediator;
        _service = service;
        _repository = repository;
        _logger = logger;
        _client = client;
        _settings = settings;
        _cache = cache;
    }

    [HttpGet("{id}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public async Task<UserDto> GetUserAsync(string id)
    {
        Guard.Against.Null(id, nameof(id));
        _logger.LogInformation("Fetching");
        if (_cache.TryGetValue(id, out var cached))
        {
            return (UserDto)cached!;
        }

        var dto = await _mediator.SendAsync(new GetUserQuery(id));
        _repository.Add(new UserEntity { Id = System.Guid.NewGuid(), Name = dto.Name });
        var remote = await _client.GetStringAsync(_settings.Value.Endpoint);
        return dto;
    }
}
""",
                ["Startup/Program.cs"] = """
using Sample;
using Sample.Configuration;
using Sample.Controllers;
using Sample.Data;
using Sample.Messaging;
using Sample.Services;
using Sample.Workers;

namespace Sample.Startup;

public static class Program
{
    public static void Configure(IServiceCollection services, WebApplication app)
    {
        services
            .AddScoped<IUserService, UserService>()
            .AddSingleton<IUserRepository, UserRepository>()
            .AddHostedService<UserWorker>()
            .AddHttpClient("users");

        services.AddMediatR();

        app.MapGet("/health", () => "ok");
    }
}
""",
                ["Validation/UserDtoValidator.cs"] = """
using System;
using Sample.Contracts;

namespace Sample.Validation;

public sealed class UserDtoValidator : AbstractValidator<UserDto>
{
    public UserDtoValidator()
    {
        RuleFor(dto => dto.Name);
    }
}
""",
                ["Workers/Namespace.cs"] = """
namespace Sample.Workers;

public sealed class UserWorker : Sample.Services.UserWorker { }
"""
            };

            var syntaxTrees = files.Select(pair => CSharpSyntaxTree.ParseText(pair.Value, path: pair.Key)).ToArray();

            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
            };

            var compilation = CSharpCompilation.Create(
                assemblyName: "FlowComprehensiveSample",
                syntaxTrees: syntaxTrees,
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var metadata = new Dictionary<string, object?>
            {
                ["Configuration"] = new Dictionary<string, object?>
                {
                    ["UserSettings"] = new Dictionary<string, object?>
                    {
                        ["Endpoint"] = "https://api.sample.local",
                        ["Timeout"] = 30
                    }
                }
            };

            var queryContext = new SelectorQueryContext(metadata: metadata);
            var roots = syntaxTrees.Select(t => t.GetCompilationUnitRoot()).ToArray();
            return (compilation, roots, queryContext);
        }
}
