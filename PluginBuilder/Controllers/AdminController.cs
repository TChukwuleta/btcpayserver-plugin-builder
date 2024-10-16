using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Admin;

namespace PluginBuilder.Controllers;

[Authorize(Roles = "ServerAdmin")]
public class AdminController : Controller
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly DBConnectionFactory _connectionFactory;

    public AdminController(UserManager<IdentityUser> userManager, RoleManager<IdentityRole> roleManager,
        DBConnectionFactory connectionFactory)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _connectionFactory = connectionFactory;
    }

    [HttpGet("plugins")]
    public async Task<IActionResult> ListPlugins()
    {
        await using var conn = await _connectionFactory.Open();
        var rows = await conn.QueryAsync($"""
                                          SELECT p.slug, p.visibility, v.ver, v.build_id, v.btcpay_min_ver, v.pre_release, v.updated_at, u."Email" as email 
                                          FROM plugins p
                                          LEFT JOIN users_plugins up ON p.slug = up.plugin_slug 
                                          LEFT JOIN "AspNetUsers" u ON up.user_id = u."Id"
                                          LEFT JOIN (
                                              SELECT DISTINCT ON (plugin_slug) plugin_slug, ver, build_id, btcpay_min_ver, pre_release, updated_at
                                              FROM versions
                                              ORDER BY plugin_slug, build_id DESC
                                          ) v ON p.slug = v.plugin_slug
                                          ORDER BY p.slug;
                                          """);
        var plugins = new List<AdminPluginViewModel>();
        foreach (var row in rows)
        {
            var plugin = new AdminPluginViewModel
            {
                ProjectSlug = row.slug, Visibility = row.visibility, PublisherEmail = row.email
            };

            if (row.ver != null)
            {
                plugin.Version = string.Join('.', row.ver);
                plugin.BuildId = row.build_id;
                plugin.BtcPayMinVer = string.Join('.', row.btcpay_min_ver);
                plugin.PreRelease = row.pre_release;
                plugin.UpdatedAt = row.updated_at;
            }
            
            plugins.Add(plugin);
        }

        return View(plugins);
    }

    // Plugin Edit
    [HttpGet("plugins/edit/{slug}")]
    public async Task<IActionResult> PluginEdit(string slug)
    {
        await using var conn = await _connectionFactory.Open();
        var plugin = await conn.QueryFirstOrDefaultAsync<PluginViewModel>(
            "SELECT * FROM plugins WHERE slug = @Slug", new { Slug = slug });
        if (plugin == null)
        {
            return NotFound();
        }

        return View(plugin);
    }

    // 
    [HttpPost("plugins/edit/{slug}")]
    public async Task<IActionResult> PluginEdit(string slug, PluginViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        await using var conn = await _connectionFactory.Open();
        var affectedRows = await conn.ExecuteAsync(
            $"""
                 UPDATE plugins 
                 SET settings = @settings::JSONB, visibility = @visibility::plugin_visibility_enum
                 WHERE slug = @slug
                """, 
            new
            {
                settings = model.Settings, 
                visibility = model.Visibility.ToString().ToLowerInvariant(),
                slug
            });
        if (affectedRows == 0)
        {
            return NotFound();
        }

        return RedirectToAction("ListPlugins");
    }
    
    // Plugin Delete
    [HttpGet("plugins/delete/{slug}")]
    public async Task<IActionResult> PluginDelete(string slug)
    {
        await using var conn = await _connectionFactory.Open();
        var plugin = await conn.QueryFirstOrDefaultAsync<PluginViewModel>(
            "SELECT * FROM plugins WHERE slug = @Slug", new { Slug = slug });
        if (plugin == null)
        {
            return NotFound();
        }

        return View(plugin);
    }

    [HttpPost("plugins/delete/{slug}")]
    public async Task<IActionResult> PluginDeleteConfirmed(string slug)
    {
        await using var conn = await _connectionFactory.Open();
        var affectedRows = await conn.ExecuteAsync(
    $"""
    DELETE FROM builds WHERE plugin_slug = @Slug;
    DELETE FROM builds_ids WHERE plugin_slug = @Slug;
    DELETE FROM builds_logs WHERE plugin_slug = @Slug;
    DELETE FROM users_plugins WHERE plugin_slug = @Slug;
    DELETE FROM versions WHERE plugin_slug = @Slug;
    DELETE FROM plugins WHERE slug = @Slug;
    """, new { Slug = slug });
        if (affectedRows == 0)
        {
            return NotFound();
        }

        return RedirectToAction("ListPlugins");
    }

    // list users
    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        var users = _userManager.Users.ToList();
        var model = new List<AdminUsersViewModel>();
        foreach (var user in users)
        {
            var userRoles = await _userManager.GetRolesAsync(user);
            model.Add(new AdminUsersViewModel
            {
                Id = user.Id,
                Email = user.Email!,
                UserName = user.UserName!,
                EmailConfirmed = user.EmailConfirmed,
                Roles = userRoles
            });
        }

        return View(model);
    }

    // edit roles
    [HttpGet("editroles/{userId}")]
    public async Task<IActionResult> EditRoles(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return NotFound();
        }

        var userRoles = await _userManager.GetRolesAsync(user);
        var allRoles = _roleManager.Roles.ToList();
        var model = new EditUserRolesViewModel
        {
            UserId = user.Id, UserName = user.UserName, UserRoles = userRoles, AvailableRoles = allRoles
        };
        return View(model);
    }

    [HttpPost("editroles/{userId}")]
    public async Task<IActionResult> EditRoles(string userId, List<string> userRoles)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return NotFound();
        }

        var currentRoles = await _userManager.GetRolesAsync(user);
        var rolesToAdd = userRoles.Except(currentRoles).ToList();
        var rolesToRemove = currentRoles.Except(userRoles).ToList();

        // Validate if this is the last admin user and prevent it from being removed from the ServerAdmin role
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (currentUserId == userId && rolesToRemove.Contains("ServerAdmin"))
        {
            var admins = await _userManager.GetUsersInRoleAsync("ServerAdmin");
            if (admins.Count == 1)
            {
                ModelState.AddModelError("", "You cannot remove yourself as the last ServerAdmin.");

                // Rebuild the view model to pass it back to the view
                var model = new EditUserRolesViewModel
                {
                    UserId = userId, UserRoles = currentRoles.ToList(), AvailableRoles = _roleManager.Roles.ToList()
                };
                return View(model);
            }
        }

        await _userManager.AddToRolesAsync(user, rolesToAdd);
        await _userManager.RemoveFromRolesAsync(user, rolesToRemove);
        return RedirectToAction("Users");
    }

    // init reset password
    [HttpGet("/admin/initpasswordreset")]
    public async Task<IActionResult> InitPasswordReset(string userId)
    {
        var model = new InitPasswordResetViewModel();
        var user = await _userManager.FindByIdAsync(userId);
        if (user != null)
        {
            model.Email = user.Email;
        }

        return View(model);
    }

    [HttpPost("/admin/initpasswordreset")]
    public async Task<IActionResult> InitPasswordReset(InitPasswordResetViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        // Require the user to have a confirmed email before they can log on.
        var user = await _userManager.FindByEmailAsync(model.Email);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "User with suggested email doesn't exist");
            return View(model);
        }

        var result = await _userManager.GeneratePasswordResetTokenAsync(user);
        model.PasswordResetToken = result;
        return View(model);
    }
}
