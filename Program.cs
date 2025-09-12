using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MyApiProject.Hubs;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// Configuración de CORS optimizada
var allowedCorsOrigins = configuration.GetSection("AllowedCorsOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowedCorsOrigins", policy =>
    {
        policy.WithOrigins(allowedCorsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Configuración de autenticación JWT optimizada
var jwtSettings = configuration.GetRequiredSection("JwtSettings");
var key = Encoding.UTF8.GetBytes(jwtSettings["Key"] ?? throw new InvalidOperationException("JWT Key is missing"));

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

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = null; // Mantiene nombres originales de las propiedades
        options.JsonSerializerOptions.MaxDepth = 64; // Un valor más razonable para evitar problemas de rendimiento
    })
    .AddNewtonsoftJson(options =>
    {
        options.SerializerSettings.MaxDepth = 64; // Limita la profundidad para evitar sobrecarga
        options.SerializerSettings.Error = (sender, args) => args.ErrorContext.Handled = true;
    });

// Registro de servicios
builder.Services.AddHttpClient();

// Registrar IMemoryCache
builder.Services.AddMemoryCache(); // Esto es necesario para resolver IMemoryCache

// ↓↓↓ Agregar SignalR a los servicios ↓↓↓
builder.Services.AddSignalR();
var swaggerGroups = new[]
{
    new { Name = "general", Title = "Intelisis - Mercdos Mejia | AresNative" },
    new { Name = "reporteria",   Title = "Reporteria" },
    new { Name = "pickUp",   Title = "PickUp" },
    new { Name = "articulos",   Title = "Articulos" },
};
// Configuración de Swagger con seguridad JWT optimizada
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    foreach (var group in swaggerGroups)
    {
        c.SwaggerDoc(group.Name, new OpenApiInfo
        {
            Title = group.Title,
            Version = "v1"
        });
    }

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
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { securityScheme, Array.Empty<string>() }
    });
});

var app = builder.Build();

// Configuración del pipeline
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    foreach (var group in swaggerGroups)
    {
        c.SwaggerEndpoint($"/swagger/{group.Name}/swagger.json", $"{group.Title} v1");
    }
    c.RoutePrefix = string.Empty; // Acceso a Swagger en la raíz
});

app.UseCors("AllowedCorsOrigins");
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapHub<Hubs>("/Hubs");
app.MapControllers();
app.Run();
