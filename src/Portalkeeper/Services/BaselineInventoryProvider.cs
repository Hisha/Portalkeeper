using System;
using System.Collections.Generic;
using Portalkeeper.Models.Runtime;

namespace Portalkeeper.Services;

// Explicit, deterministic baseline allowlist for the supported stock client
// (WoW 3.3.5a, build 12340). Checkpoint 1 established the BaselineManifest
// model but carried only a category-level starter; Checkpoint 2 supplies the
// file-level allowlist used by runtime construction.
//
// IMPORTANT provenance rules honoured here:
//   * The set of inherited paths is an explicit allowlist. Nothing is inferred
//     by scanning the source client's Data dir or Interface/AddOns, and a
//     "Blizzard_" name prefix is never trusted on its own.
//   * No SHA-256 values are invented. The repository has no authoritative
//     stock-client hash inventory (realm/client ExecutableSHA256 is the single
//     authoritative hash, supplied by the realm for Wow.exe). Every asset below
//     is therefore path-allowlisted only; the runtime manifest records that
//     explicitly and the runtime validator verifies what it can (file identity
//     for hard links, existence, containment) without pretending incomplete
//     provenance is complete.
//   * The root file inventory mirrors the reference 3.3.5a client root
//     (a clean client whose Wow.exe SHA-256 is aa63a5...cb8). Runtime repair
//     utilities Repair.exe and Scan.dll are deliberately excluded: they are not
//     required to run the client.
//   * The locale archive inventory is expressed as per-locale templates derived
//     from the reference client's Data/enUS directory and the archive order
//     already used by the repository's client previews.
//   * The loose Blizzard addon inventory records exactly the 23 stock addon
//     stub files present in the reference client. The stock UI generally lives
//     inside the MPQ archives; these loose files are inherited individually,
//     never by trusting the Blizzard_ prefix.
public sealed class BaselineInventoryProvider
{
    public static readonly string[] RootCopiedFiles =
    {
        "Wow.exe",
        "WowError.exe",
        "Battle.net.dll",
        "DivxDecoder.dll",
        "dbghelp.dll",
        "ijl15.dll",
        "msvcr80.dll",
        "unicows.dll"
    };

    public static readonly string[] GlobalDataMpqs =
    {
        "common.MPQ",
        "common-2.MPQ",
        "expansion.MPQ",
        "lichking.MPQ",
        "patch.MPQ",
        "patch-2.MPQ",
        "patch-3.MPQ"
    };

    // Required playable-content archive templates for a bootable locale.
    public static readonly string[] RequiredLocaleMpqs =
    {
        "locale-{locale}.MPQ",
        "expansion-locale-{locale}.MPQ",
        "lichking-locale-{locale}.MPQ",
        "patch-{locale}.MPQ",
        "patch-{locale}-2.MPQ",
        "patch-{locale}-3.MPQ"
    };

    // Optional voice/base archive templates from the reference locale tree.
    // Inherited only when the source actually contains them.
    public static readonly string[] OptionalLocaleMpqs =
    {
        "speech-{locale}.MPQ",
        "expansion-speech-{locale}.MPQ",
        "lichking-speech-{locale}.MPQ",
        "base-{locale}.MPQ",
        "backup-{locale}.MPQ"
    };

    // The 23 stock Blizzard addon stub files present in the reference client.
    // Required=false: inherited individually only when present in the source,
    // so a source client that ships a different loose addon layout is not
    // rejected outright. This is path-allowlist only.
    public static readonly string[] BlizzardBaselineAddons =
    {
        "Blizzard_AchievementUI",
        "Blizzard_ArenaUI",
        "Blizzard_AuctionUI",
        "Blizzard_BarbershopUI",
        "Blizzard_BattlefieldMinimap",
        "Blizzard_BindingUI",
        "Blizzard_Calendar",
        "Blizzard_CombatLog",
        "Blizzard_CombatText",
        "Blizzard_DebugTools",
        "Blizzard_GMChatUI",
        "Blizzard_GMSurveyUI",
        "Blizzard_GlyphUI",
        "Blizzard_GuildBankUI",
        "Blizzard_InspectUI",
        "Blizzard_ItemSocketingUI",
        "Blizzard_MacroUI",
        "Blizzard_RaidUI",
        "Blizzard_TalentUI",
        "Blizzard_TimeManager",
        "Blizzard_TokenUI",
        "Blizzard_TradeSkillUI",
        "Blizzard_TrainerUI"
    };

    public BaselineManifest BuildForLocale(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ManagedPath.Relative(locale, fileName: true);

        var assets = new List<BaselineAsset>();

        foreach (var file in RootCopiedFiles)
        {
            assets.Add(FileAsset("Root " + file, file, BaselineAssetCategory.Root));
        }

        foreach (var mpq in GlobalDataMpqs)
        {
            assets.Add(FileAsset("Data " + mpq, "Data/" + mpq, BaselineAssetCategory.DataMpq));
        }

        foreach (var template in RequiredLocaleMpqs)
        {
            var name = Expand(template, locale);
            assets.Add(FileAsset("Locale " + name, "Data/" + locale + "/" + name, BaselineAssetCategory.LocaleMpq));
        }

        foreach (var template in OptionalLocaleMpqs)
        {
            var name = Expand(template, locale);
            assets.Add(FileAsset("Optional locale " + name, "Data/" + locale + "/" + name, BaselineAssetCategory.LocaleMpq, required: false));
        }

        foreach (var addon in BlizzardBaselineAddons)
        {
            var file = addon + "/" + addon + ".pub";
            assets.Add(FileAsset("Addon " + file, "Interface/AddOns/" + file, BaselineAssetCategory.BaselineAddOn, required: false));
        }

        return new BaselineManifest
        {
            Name = "wow-3.3.5a-12340-explicit",
            Version = "3.3.5a",
            Build = "12340",
            Assets = assets
        };
    }

    private static BaselineAsset FileAsset(
        string description,
        string relativePath,
        BaselineAssetCategory category,
        bool required = true)
    {
        return new BaselineAsset
        {
            RelativePath = relativePath,
            Kind = BaselineAssetKind.File,
            Category = category,
            Immutable = true,
            Required = required
        };
    }

    private static string Expand(string template, string locale) =>
        template.Replace("{locale}", locale, StringComparison.Ordinal);
}