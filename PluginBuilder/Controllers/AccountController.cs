using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using PluginBuilder.APIModels;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;
using PluginBuilder.Constants;
using Microsoft.AspNetCore.Http;

namespace PluginBuilder.Controllers
{
    [Authorize]
    public class AccountController : Controller
    {
        private DBConnectionFactory ConnectionFactory { get; }
        private UserManager<IdentityUser> UserManager { get; }
        private readonly PgpKeyService _pgpKeyService;

        public AccountController(
            PgpKeyService pgpKeyService,
            DBConnectionFactory connectionFactory,
            UserManager<IdentityUser> userManager)
        {
            ConnectionFactory = connectionFactory;
            _pgpKeyService = pgpKeyService;
            UserManager = userManager;
        }

        [HttpGet("details")]
        public async Task<IActionResult> AccountDetails()
        {
            await using var conn = await ConnectionFactory.Open();
            var settings = await conn.GetAccountDetailSettings(UserManager.GetUserId(User)!);
            return View(settings);
        }


        [HttpPost("details")]
        public async Task<IActionResult> AccountDetails(AccountSettings model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            await using var conn = await ConnectionFactory.Open();
            var user = UserManager.GetUserId(User)!;
            var accountSettings = await conn.GetAccountDetailSettings(user) ?? model;

            accountSettings.Nostr = model.Nostr ?? accountSettings.Nostr;
            accountSettings.Twitter = model.Twitter ?? accountSettings.Twitter;
            accountSettings.Github = model.Github ?? accountSettings.Github;
            accountSettings.Email = model.Email ?? accountSettings.Email;

            await conn.SetAccountDetailSettings(accountSettings, user);

            TempData[TempDataConstant.SuccessMessage] = "Account details updated successfully";
            return RedirectToAction(nameof(AccountDetails));
        }

        [HttpPost("saveaccountkeys")]
        public async Task<IActionResult> SaveAccountPgpKeys(AccountKeySettingsViewModel model)
        {
            try
            {
                await _pgpKeyService.AddNewPGGKeyAsync(model.PublicKey, model.Title, UserManager.GetUserId(User));
                TempData[WellKnownTempData.SuccessMessage] = "Account key added successfully";
                return RedirectToAction("AccountKeySettings");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", ex.Message);
                return RedirectToAction("AccountKeySettings");
            }
        }

        [HttpGet("accountkeysettings")]
        public async Task<IActionResult> AccountKeySettings()
        {
            await using var conn = await ConnectionFactory.Open();
            string userId = UserManager.GetUserId(User);
            var accountSettings = await conn.GetAccountDetailSettings(userId) ?? new AccountSettings();

            var pgpKeyViewModels = accountSettings?.PgpKeys?
            .GroupBy(k => k.KeyBatchId)
            .Select(g => new PgpKeyViewModel
            {
                BatchId = g.FirstOrDefault()?.KeyBatchId,
                Title = g.FirstOrDefault()?.Title,
                KeyUserId = g.FirstOrDefault()?.KeyUserId,
                KeyId = g.FirstOrDefault(k => k.IsMasterKey)?.KeyId, 
                Subkeys = string.Join(", ", g.Where(k => !k.IsMasterKey).Select(k => k.KeyId)),
                AddedDate = g.FirstOrDefault()?.AddedDate
            })
            .ToList();
            return View(pgpKeyViewModels);
        }

        [HttpPost("deleteaccountkey/{batchId}")]
        public async Task<IActionResult> DeleteAccountPgpKey(string batchId)
        {
            await using var conn = await ConnectionFactory.Open();
            string userId = UserManager.GetUserId(User);
            var accountSettings = await conn.GetAccountDetailSettings(userId);
            if (accountSettings == null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Account settings not found";
                return RedirectToAction("AccountKeySettings");
            }
            int removedCount = accountSettings.PgpKeys?.RemoveAll(k => k.KeyBatchId == batchId) ?? 0;
            if (removedCount == 0)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Invalid key batch";
                return RedirectToAction("AccountKeySettings");
            }
            await conn.SetAccountDetailSettings(accountSettings, userId);
            TempData[WellKnownTempData.SuccessMessage] = "Account key deleted successfully";
            return RedirectToAction("AccountKeySettings");
        }


        [HttpPost("pluginstatus/update/{action}")]
        public async Task<IActionResult> PluginStatusUpdate(PluginApprovalStatusUpdateViewModel model, string action)
        {
            await using var conn = await ConnectionFactory.Open();
            string userId = UserManager.GetUserId(User);
            if (await conn.UserOwnsPlugin(userId, model.PluginSlug))
            {
                TempData[WellKnownTempData.ErrorMessage] = "Cannot approve or reject plugin created by you";
                return RedirectToAction(nameof(PluginDetails), "Account", new { pluginSlug = model.PluginSlug });
            }
            var accountSettings = await conn.GetAccountDetailSettings(userId);
            if (accountSettings == null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Account settings not found";
                return RedirectToAction(nameof(PluginDetails), "Account", new { pluginSlug = model.PluginSlug });
            }
            List<string> publicKeys = accountSettings.PgpKeys.Select(key => key.PublicKey).ToList();
            if (!publicKeys.Any())
            {
                TempData[WellKnownTempData.ErrorMessage] = "Kindly add new GPG Keys to proceed with plugin action";
                return RedirectToAction(nameof(PluginDetails), "Account", new { pluginSlug = model.PluginSlug });
            }
            var validateSignature = _pgpKeyService.VerifyPgpMessage(model, publicKeys);
            if (!validateSignature.success)
            {
                TempData[WellKnownTempData.ErrorMessage] = validateSignature.response;
                return RedirectToAction(nameof(PluginDetails), "Account", new { pluginSlug = model.PluginSlug });
            }
            var row = await conn.QueryFirstOrDefaultAsync<(int[] ver, bool pre_release, string reviews)>(
            "SELECT v.ver, v.pre_release, v.reviews FROM versions v " +
            "LEFT JOIN users_plugins up ON v.plugin_slug=up.plugin_slug WHERE up.plugin_slug=@pluginSlug",
            new { pluginSlug = model.PluginSlug });

            var reviews = string.IsNullOrEmpty(row.reviews) || row.reviews == "{}" ? new List<PluginReview>() : JsonConvert.DeserializeObject<List<PluginReview>>(row.reviews);
            bool hasUserReviewed = reviews?.Any(review => review.UserId == userId) ?? false;
            if (hasUserReviewed)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Cannot complete action as you have already actioned on this plugin";
                return RedirectToAction(nameof(PluginDetails), "Account", new { pluginSlug = model.PluginSlug });
            }

            if (action == "approve" || action == "reject")
            {
                reviews.Add(new PluginReview
                {
                    Comment = model.Message,
                    DateActioned = DateTime.Now,
                    Status = action,
                    UserId = userId
                });
                await conn.SetVersionReview(model.PluginSlug, row.ver, reviews);
                string actionMessage = action == "approve" ? "approved" : "rejected";
                TempData[WellKnownTempData.SuccessMessage] = $"{model.PluginSlug} {actionMessage} successfully";
            }
            else
            {
                TempData[WellKnownTempData.ErrorMessage] = "Invalid action";
                return RedirectToAction(nameof(PluginDetails), "Account", new { pluginSlug = model.PluginSlug });
            }
            return RedirectToAction(nameof(PluginDetails), "Account", new { pluginSlug = model.PluginSlug });
        }


        [HttpGet("listplugins")]
        public async Task<IActionResult> ListPlugins(
        [ModelBinder(typeof(PluginVersionModelBinder))] PluginVersion? btcpayVersion = null)
        {
            await using var conn = await ConnectionFactory.Open();

            var rows = await conn.QueryAsync<(string plugin_slug, int[] ver, string settings, long id, string manifest_info, string build_info)>(
            $"SELECT lv.plugin_slug, lv.ver, p.settings, b.id, b.manifest_info, b.build_info FROM get_all_versions(@btcpayVersion, @includePreRelease) lv " +
            "JOIN builds b ON b.plugin_slug = lv.plugin_slug AND b.id = lv.build_id " +
            "JOIN plugins p ON b.plugin_slug = p.slug " +
            "WHERE b.manifest_info IS NOT NULL AND b.build_info IS NOT NULL " +
            "ORDER BY manifest_info->>'Name'",
            new
            {
                btcpayVersion = btcpayVersion?.VersionParts,
                includePreRelease = false
            });

            var versions = rows
                .Select(r => new PublishedVersion
                {
                    ProjectSlug = r.plugin_slug,
                    ManifestInfo = JObject.Parse(r.manifest_info),
                    Documentation = JsonConvert.DeserializeObject<PluginSettings>(r.settings)?.Documentation
                }).ToList();
            return View(versions);
        }
        
        [HttpGet("plugindetails/{pluginSlug}")]
        public async Task<IActionResult> PluginDetails(string pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))] PluginVersion? btcpayVersion = null)
        {
            await using var conn = await ConnectionFactory.Open();
            string userId = UserManager.GetUserId(User);

            var row = await conn.QueryFirstOrDefaultAsync<(string plugin_slug, int[] ver, string settings, long id, string manifest_info, string build_info, string reviews)>(
                $"SELECT lv.plugin_slug, lv.ver, p.settings, b.id, b.manifest_info, b.build_info, v.reviews  FROM get_all_versions(@btcpayVersion, @includePreRelease) lv " +
                "JOIN builds b ON b.plugin_slug = lv.plugin_slug AND b.id = lv.build_id " +
                "JOIN plugins p ON b.plugin_slug = p.slug " +
                "JOIN versions v ON v.plugin_slug = lv.plugin_slug " +
                "WHERE b.manifest_info IS NOT NULL AND b.build_info IS NOT NULL AND lv.plugin_slug = @pluginSlug " +
                "ORDER BY manifest_info->>'Name'",
                new
                {
                    btcpayVersion = btcpayVersion?.VersionParts,
                    includePreRelease = false,
                    pluginSlug
                });

            if (string.IsNullOrEmpty(row.plugin_slug))
            {
                return NotFound();
            }

            var plugin = new ExtendedPublishedVersion
            {
                ProjectSlug = row.plugin_slug,
                Version = string.Join('.', row.ver),
                BuildId = row.id,
                BuildInfo = JObject.Parse(row.build_info),
                ManifestInfo = JObject.Parse(row.manifest_info),
                Reviews = string.IsNullOrEmpty(row.reviews) || row.reviews == "{}" ? new List<PluginReview>() : JsonConvert.DeserializeObject<List<PluginReview>>(row.reviews),
                Documentation = JsonConvert.DeserializeObject<PluginSettings>(row.settings)!.Documentation,
                HasPublishedPlugin = await conn.UserHasPublishedPlugin(userId)
            };
            return View(plugin);
        }
    }
}
