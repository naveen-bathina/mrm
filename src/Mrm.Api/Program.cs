using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Mrm.Api.Auth;
using Mrm.Api.Movies;
using Mrm.Infrastructure;
using Mrm.Infrastructure.Entities;

var builder = WebApplication.CreateBuilder(args);

// Database
builder.Services.AddDbContext<MrmDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// JWT Authentication
var jwtSection = builder.Configuration.GetSection("Jwt");
var key = Encoding.UTF8.GetBytes(jwtSection["Key"]!);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(key),
            RoleClaimType = ClaimNames.Role,
        };
    });

// Authorization policies
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.StudioAdmin, p =>
        p.RequireAuthenticatedUser()
         .RequireRole(nameof(UserRole.StudioAdmin)))
    .AddPolicy(AuthPolicies.ProductionManager, p =>
        p.RequireAuthenticatedUser()
         .RequireRole(nameof(UserRole.ProductionManager)))
    .AddPolicy(AuthPolicies.SystemAdmin, p =>
        p.RequireAuthenticatedUser()
         .RequireRole(nameof(UserRole.SystemAdmin)));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapMovieEndpoints();

app.Run();

// Allow test project to reference this type
public partial class Program { }
