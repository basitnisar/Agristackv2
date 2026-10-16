//using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
//using Microsoft.IdentityModel.Protocols.WSIdentity;
using AgristackV2.DTO;
using AgristackV2.Application.Interfaces;
//using AgristackV2.Infrastructure.Persistence;
using System.Security.Cryptography;
using System.Text;
using AgristackV2.Services;



namespace AgristackV2.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(
    TokenService tokenService) : ControllerBase
{
   
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequests request)
    {
        // Temporary hardcoded credentials
        const string username = "Basit";
        const string password = "Basit@123";
        const int userId = 1;
        const string role = "Admin";

        // Validate credentials
        if (request == null ||
            request.Email != username ||
            request.Password != password)
        {
            return Unauthorized(new
            {
                message = "Invalid username or password."
            });
        }

        // Generate JWT token
        var token = tokenService.GenerateToken(
            username,
            userId,
            role);

        // Token expiry (must match TokenService)
        var expiresAt = DateTime.UtcNow.AddMinutes(60);

        return Ok(new LoginResponse(
            token,
            username,
            role,
            userId,
            expiresAt));
    }

}