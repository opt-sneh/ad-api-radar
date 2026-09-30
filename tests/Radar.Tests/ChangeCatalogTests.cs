using Radar;

namespace Radar.Tests;

[TestClass]
public sealed class ChangeCatalogTests
{
    [TestMethod]
    public void RemovedResourceCollapsesItsFields()
    {
        WithModels((before, after) =>
        {
            var changes = ChangeCatalog.Compare(before, after);
            var removed = changes.Single(c => c.Kind == "RESOURCE_REMOVED" && c.Symbol == "removed_resource");
            Assert.HasCount(2, removed.RemovedFields);
            Assert.IsFalse(changes.Any(c => c.Symbol.StartsWith("removed_resource.", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public void BusinessLocationHasProfileLocationCandidate()
    {
        WithModels((before, after) =>
        {
            var change = ChangeCatalog.Compare(before, after)
                .Single(c => c.Symbol == "smart_campaign_setting.business_location");
            Assert.AreEqual("FIELD_RENAMED_CANDIDATE", change.Kind);
            Assert.IsTrue(change.ReplacementCandidates.Any(c =>
                c.Symbol == "resources.SmartCampaignSetting.business_profile_location" && c.Score >= 0.5));
        });
    }

    [TestMethod]
    public void ExtensionAssetUsesKnownTokenSwap()
    {
        WithModels((before, after) =>
        {
            var candidate = ChangeCatalog.Compare(before, after)
                .Single(c => c.Symbol == "smart_campaign_setting.extension")
                .ReplacementCandidates.Single(c => c.Symbol.EndsWith(".asset", StringComparison.Ordinal));
            Assert.AreEqual("known token swap", candidate.Reason);
        });
    }

    [TestMethod]
    public void StaleFieldGetsCandidatesFromLatestMessage()
    {
        WithModels((_, after) =>
        {
            var candidates = ChangeCatalog.FindCandidates(
                "smart_campaign_setting.business_location", [after]);
            Assert.IsTrue(candidates.Any(c =>
                c.Symbol == "resources.SmartCampaignSetting.business_profile_location"));
        });
    }

    [TestMethod]
    public void StaleRecommendationFieldsUseTokenSwaps()
    {
        WithModels((_, after) =>
        {
            var call = ChangeCatalog.FindCandidates("recommendation.call_extension_recommendation", [after]);
            Assert.IsTrue(call.Any(c => c.Symbol.EndsWith(".call_asset_recommendation", StringComparison.Ordinal)
                && c.Reason == "known token swap"));
            var rule = ChangeCatalog.FindCandidates("recommendation.combined_rule_user_list", [after]);
            Assert.IsTrue(rule.Any(c => c.Symbol.EndsWith(".flexible_rule_user_list", StringComparison.Ordinal)
                && c.Reason == "known token swap"));
        });
    }

    [TestMethod]
    public void TypeRenamesHaveCandidatesAndReleaseNotePriority()
    {
        WithModels((before, after) =>
        {
            var notes = ChangeCatalog.ParseReleaseNotes(File.ReadAllText(Fixture("release-notes.html")));
            var changes = ChangeCatalog.Compare(before, after, notes: notes);
            foreach (var (message, candidateName) in new[]
            {
                ("Incentive", "incentive_type"),
                ("IncentiveOffer", "offer_type"),
                ("FetchIncentiveRequest", "incentive_type"),
            })
            {
                var change = changes.Single(c => c.Symbol == $"services.{message}.type");
                Assert.AreEqual("FIELD_RENAMED_CANDIDATE", change.Kind);
                Assert.IsTrue(change.ReplacementCandidates.Any(c =>
                    c.Symbol == $"services.{message}.{candidateName}" && c.Score >= 0.5));
            }
            Assert.AreEqual("release-note", changes.Single(c => c.Symbol == "services.Incentive.type")
                .ReplacementCandidates[0].Reason);
        });
    }

    [TestMethod]
    public void BiddableKeywordsSuggestsKeywords()
    {
        WithModels((before, after) =>
        {
            var change = ChangeCatalog.Compare(before, after)
                .Single(c => c.Symbol == "services.ForecastAdGroup.biddable_keywords");
            Assert.AreEqual("FIELD_RENAMED_CANDIDATE", change.Kind);
            Assert.IsTrue(change.ReplacementCandidates.Any(c =>
                c.Symbol == "services.ForecastAdGroup.keywords" && c.Score >= 0.5));
        });
    }

    [TestMethod]
    public void EarlierAddedPluralFieldRemainsCandidate()
    {
        WithModels((before, after, fixtureRoot) =>
        {
            string versionRoot = Path.Combine(fixtureRoot, "google", "ads", "googleads", "v97");
            foreach (string part in new[] { "", "-common", "-enums", "-services" })
            {
                string package = part switch
                {
                    "" => "resources", "-common" => "common",
                    "-enums" => "enums", _ => "services",
                };
                string text = File.ReadAllText(Fixture("v98" + part + ".proto"))
                    .Replace("v98", "v97", StringComparison.Ordinal);
                if (part == "-services")
                    text = text.Replace(" repeated int64 plannable_location_ids = 2;", "",
                        StringComparison.Ordinal);
                string target = Path.Combine(versionRoot, package, "v97" + part + ".proto");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, text);
            }
            string pb = Path.Combine(fixtureRoot, "v97.pb");
            File.WriteAllBytes(pb, new ProtoCompiler(RepoRoot()).Compile(fixtureRoot, "v97", out _));
            var earlier = SchemaModel.Load(pb, "v97");
            var change = ChangeCatalog.Compare(before, after, [earlier, before, after])
                .Single(c => c.Symbol == "services.Targeting.plannable_location_id");
            Assert.AreEqual("FIELD_RENAMED_CANDIDATE", change.Kind);
            Assert.IsTrue(change.ReplacementCandidates.Any(c =>
                c.Symbol == "services.Targeting.plannable_location_ids" && c.Score >= 0.5));
        });
    }

    [TestMethod]
    public void CookieFrequencyCapSuggestsSetting()
    {
        WithModels((before, after) =>
        {
            var change = ChangeCatalog.Compare(before, after)
                .Single(c => c.Symbol == "services.GenerateReachForecastRequest.cookie_frequency_cap");
            Assert.AreEqual("FIELD_RENAMED_CANDIDATE", change.Kind);
            Assert.IsTrue(change.ReplacementCandidates.Any(c =>
                c.Symbol == "services.GenerateReachForecastRequest.cookie_frequency_cap_setting"
                && c.Score >= 0.5));
        });
    }

    [TestMethod]
    public void LifecycleGoalDoesNotSuggestLiftMeasurement()
    {
        WithModels((before, after) =>
        {
            var change = ChangeCatalog.Compare(before, after)
                .Single(c => c.Symbol == "services.GoogleAdsRow.campaign_lifecycle_goal");
            Assert.AreEqual("FIELD_REMOVED", change.Kind);
            Assert.HasCount(0, change.ReplacementCandidates);
        });
    }

    [TestMethod]
    public void SuggestCommandReadsLatestSnapshot()
    {
        WithModels((_, _, fixtureRoot) =>
        {
            string google = Path.Combine(fixtureRoot, "google");
            File.Copy(Path.Combine(fixtureRoot, "v99.pb"), Path.Combine(google, "v99.pb"));
            Assert.AreEqual(0, SuggestCommand.Run([
                "--path", "smart_campaign_setting.business_location",
                "--version", "v99", "--snapshots", fixtureRoot,
            ]));
        });
    }

    [TestMethod]
    public void RequiredFieldBehaviorIsDetected()
    {
        WithModels((before, after) =>
        {
            Assert.IsFalse(before.ProtoFields["resources.Campaign.required_later"].Required);
            Assert.IsTrue(after.ProtoFields["resources.Campaign.required_later"].Required);
            Assert.IsTrue(ChangeCatalog.Compare(before, after).Any(c =>
                c.Kind == "FIELD_BECAME_REQUIRED" && c.Symbol == "campaign.required_later"));
        });
    }

    [TestMethod]
    public void ServiceMessageFieldsAreCompared()
    {
        WithModels((before, after) =>
        {
            Assert.IsTrue(before.ProtoFields.ContainsKey("services.GenerateBenchmarksMetricsResponse.customer_metrics"));
            var changes = ChangeCatalog.Compare(before, after);
            Assert.IsTrue(changes.Any(c => c.Kind == "FIELD_TYPE_CHANGED"
                && c.Symbol == "services.GenerateBenchmarksMetricsResponse.customer_metrics"));
            Assert.IsTrue(changes.Any(c => c.Kind == "FIELD_REMOVED"
                && c.Symbol == "services.GenerateBenchmarksMetricsResponse.removed_detail"));
        });
    }

    [TestMethod]
    public void AddedEnumValueIsSilentRisk()
    {
        WithModels((before, after) =>
        {
            var change = ChangeCatalog.Compare(before, after)
                .Single(c => c.Kind == "ENUM_VALUE_ADDED" && c.Symbol == "Status.ADDED");
            Assert.AreEqual("silent-risk", change.Severity);
        });
    }

    [TestMethod]
    public void CsharpNamesFollowProtoConventions()
    {
        WithModels((_, after) =>
        {
            Assert.AreEqual("VideoBrandSafetySuitability",
                after.ProtoFields["resources.Campaign.video_brand_safety_suitability"].CsharpName);
            Assert.AreEqual("AdTypeEnum.Types.AdType.CallAd",
                after.Enums["AdType.CALL_AD"].CsharpName);
            Assert.AreEqual("CampaignServiceClient", after.ServiceNames["CampaignService"].CsharpName);
        });
    }

    [TestMethod]
    public void ReleaseNoteIsLinkedOnlyToMatchingVersion()
    {
        WithModels((before, after) =>
        {
            var notes = ChangeCatalog.ParseReleaseNotes(File.ReadAllText(Fixture("release-notes.html")));
            var change = ChangeCatalog.Compare(before, after, notes: notes)
                .Single(c => c.Symbol == "campaign.removed_field");
            Assert.IsTrue(change.ReleaseNoteSnippets.Any(s => s.Contains("new note", StringComparison.Ordinal)));
            Assert.IsFalse(change.ReleaseNoteSnippets.Any(s => s.Contains("old note", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public void ChangeIdsAreStable()
    {
        WithModels((before, after) =>
        {
            var first = ChangeCatalog.Compare(before, after).Select(c => c.Id).ToArray();
            var second = ChangeCatalog.Compare(before, after).Select(c => c.Id).ToArray();
            CollectionAssert.AreEqual(first, second);
        });
    }

    private static void WithModels(Action<SchemaModel, SchemaModel> check) =>
        WithModels((before, after, _) => check(before, after));

    private static void WithModels(Action<SchemaModel, SchemaModel, string> check)
    {
        string root = RepoRoot();
        string fixtureRoot = Path.Combine(root, "data", "test-fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            string api = Path.Combine(fixtureRoot, "google", "api", "resource.proto");
            Directory.CreateDirectory(Path.GetDirectoryName(api)!);
            File.Copy(Fixture("resource.proto"), api);
            var compiler = new ProtoCompiler(root);
            var models = new List<SchemaModel>();
            foreach (string version in new[] { "v98", "v99" })
            {
                string versionRoot = Path.Combine(fixtureRoot, "google", "ads", "googleads", version);
                foreach (string part in new[] { "", "-common", "-enums", "-services" })
                {
                    string package = part switch
                    {
                        "" => "resources", "-common" => "common",
                        "-enums" => "enums", _ => "services",
                    };
                    string target = Path.Combine(versionRoot, package, version + part + ".proto");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Fixture(version + part + ".proto"), target);
                }
                string pb = Path.Combine(fixtureRoot, version + ".pb");
                File.WriteAllBytes(pb, compiler.Compile(fixtureRoot, version, out _));
                models.Add(SchemaModel.Load(pb, version));
            }
            check(models[0], models[1], fixtureRoot);
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AdApiRadar.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
