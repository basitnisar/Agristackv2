namespace AgristackV2.DTO
{
    public record LoginResponse(
       string Token,
       string Username,
       string Role,
       int UserId,
       DateTime ExpiresAt
   );
}
