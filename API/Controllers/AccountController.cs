using Newtonsoft.Json;

namespace API.Controllers;

// [AllowAnonymous]
[ApiController]
[Route("api/[controller]")]
public class AccountController : ControllerBase
{
    private readonly UserManager<AppUser> _userManager;
    private readonly SignInManager<AppUser> _signInManager;
    private readonly TokenService _tokenService;
    private readonly IConfiguration _config;
    private readonly HttpClient _httpClient;

    public AccountController(UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager, TokenService tokenService,
     IConfiguration config)
    {
        this._config = config;
        _tokenService = tokenService;
        _signInManager = signInManager;
        _userManager = userManager;
        _httpClient = new HttpClient
        {
            BaseAddress = new System.Uri("https://graph.facebook.com")
        };

    }

    //Allow Annoymous for the Login Endpoint
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<UserDto>> Login(LoginDto loginDto)
    {
        //Add Generic Join for the Photos Get the Current email of User and bind to the logindto email or payload
        var user = await _userManager.Users.Include(p => p.Photos)
        .FirstOrDefaultAsync(x => x.Email == loginDto.Email);

        //return 401 kapag invalid
        if (user == null) return Unauthorized();
        //supply the microsoft identity on App Use= Username , Password , at Lockout on Failure na boolean
        var result = await _signInManager.CheckPasswordSignInAsync(user, loginDto.Password, false);

        //Kapag Success na navalidate yung credentials
        if (result.Succeeded)
        {
            //Set the refresh token on the user with validity of 7 days
            await SetRefreshToken(user);
            return CreateUserObject(user);
        }
        return Unauthorized();
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<ActionResult<UserDto>> Register(RegisterDto registerDto)
    {
        if (await _userManager.Users.AnyAsync(x => x.Email == registerDto.Email))
        {
            ModelState.AddModelError("email", "Email taken");
            return ValidationProblem();
        }

        if (await _userManager.Users.AnyAsync(x => x.UserName == registerDto.Username))
        {
            ModelState.AddModelError("username", "Username taken");
            return ValidationProblem();
            // return BadRequest("Username taken");
        }

        var user = new AppUser
        {
            DisplayName = registerDto.DisplayName,
            Email = registerDto.Email,
            UserName = registerDto.Username
        };

        var result = await _userManager.CreateAsync(user, registerDto.Password);

        if (result.Succeeded)
        {
            await SetRefreshToken(user);
            return CreateUserObject(user);
        }

        return BadRequest("Problem registering user");
    }

    [Authorize]
    [HttpGet]
    public async Task<ActionResult<UserDto>> GetCurrentUser()
    {
        var user = await _userManager.Users.Include(p => p.Photos)
        .FirstOrDefaultAsync(x => x.Email == User.FindFirstValue(ClaimTypes.Email));
        await SetRefreshToken(user);
        return CreateUserObject(user);
    }

    [AllowAnonymous]
    [HttpPost("fbLogin")]
    public async Task<ActionResult<UserDto>> FacebookLogin(string accessToken)
    {
        var fbVerifyKeys = _config["Facebook:AppId"] + "|" + _config["Facebook:AppSecret"];

        var verifyToken = await _httpClient
        .GetAsync($"debug_token?input_token={accessToken}&access_token={fbVerifyKeys}");

        if (!verifyToken.IsSuccessStatusCode) return Unauthorized();

        var fbUrl = $"me?access_token={accessToken}&fields=name,email,picture.width(100).height(100)";

        var response = await _httpClient.GetAsync(fbUrl);

        if (!response.IsSuccessStatusCode) return Unauthorized();


        var fbInfo = JsonConvert.DeserializeObject<dynamic>(await response.Content.ReadAsStringAsync());

        var username = (string)fbInfo.id;

        var user = await _userManager.Users.Include(p => p.Photos)
        .FirstOrDefaultAsync(x => x.UserName == username);

        if (user != null) return CreateUserObject(user);

        user = new AppUser
        {
            DisplayName = (string)fbInfo.name,
            Email = (string)fbInfo.email,
            UserName = (string)fbInfo.id,
            Photos = new List<Photo>
                {
                 new Photo
                 {
                    Id = "fb_" + (string)fbInfo.id,
                    Url = (string)fbInfo.picture.data.url,
                    IsMain = true

                }}
        };

        var result = await _userManager.CreateAsync(user);

        if (!result.Succeeded) return BadRequest("Problem creating user account");

        await SetRefreshToken(user);
        return CreateUserObject(user);

    }

    [Authorize]
    [HttpPost("refreshToken")]
    public async Task<ActionResult<UserDto>> RefreshToken()
    {

        var refreshToken = Request.Cookies["refreshToken"];
        var user = await _userManager.Users
        .Include(r => r.RefreshTokens)
        .Include(p => p.Photos)
        .FirstOrDefaultAsync(x => x.UserName == User.FindFirstValue(ClaimTypes.Name));

        if (user == null) return Unauthorized();

        var oldToken = user.RefreshTokens.SingleOrDefault(x => x.Token == refreshToken);

        if (oldToken != null && !oldToken.IsActive) return Unauthorized();

        return CreateUserObject(user);

    }


    private async Task SetRefreshToken(AppUser user)
    {
        //I Aacess niya yung token Under API/Services/TokenService
        var refreshToken = _tokenService.GenerateRefreshToken();

        user.RefreshTokens.Add(refreshToken);
        await _userManager.UpdateAsync(user);

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Expires = DateTime.UtcNow.AddDays(7)
        };

        Response.Cookies.Append("refreshToken", refreshToken.Token, cookieOptions);
    }


    private UserDto CreateUserObject(AppUser user)
    {
        return new UserDto
        {
            DisplayName = user.DisplayName,
            Image = user?.Photos?.FirstOrDefault(x => x.IsMain)?.Url,
            Token = _tokenService.CreateToken(user),
            Username = user.UserName

        };
    }

}