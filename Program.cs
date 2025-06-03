// Program.cs
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using MyApiProject.Services;  // <-- para IAIService

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// CORS
var allowedCorsOrigins = configuration.GetSection("AllowedCorsOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowedCorsOrigins", policy =>
        policy.WithOrigins(allowedCorsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// JWT Authentication
var jwtSettings = configuration.GetRequiredSection("JwtSettings");
var key = Encoding.UTF8.GetBytes(jwtSettings["Key"]
             ?? throw new InvalidOperationException("JWT Key is missing"));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(key)
        };
        options.SaveToken = true;
    });

// Controllers + JSON settings
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = null;
        o.JsonSerializerOptions.MaxDepth = 64;
    })
    .AddNewtonsoftJson(o =>
    {
        o.SerializerSettings.MaxDepth = 64;
        o.SerializerSettings.Error = (sender, args) => args.ErrorContext.Handled = true;
    });

// Caching
builder.Services.AddMemoryCache();

// HttpClient + IA service
builder.Services.AddHttpClient<IAIService, FreeHuggingFaceAIService>();

// Utilidades y otros servicios
builder.Services.AddScoped<AuthUtils>();
builder.Services.AddScoped<TokensUtils>();

// Swagger + JWT in UI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "My API", Version = "v1" });
    c.OperationFilter<FileUploadOperationFilter>();

    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Ingrese el token JWT en este formato: Bearer {token}",
        Reference = new OpenApiReference
        {
            Type = ReferenceType.SecurityScheme,
            Id = "Bearer"
        }
    };
    c.AddSecurityDefinition("Bearer", securityScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement {
        { securityScheme, Array.Empty<string>() }
    });
});

var app = builder.Build();

// Middleware pipeline
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1");
    c.RoutePrefix = string.Empty;
});

app.UseCors("AllowedCorsOrigins");
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.Run();
