using Google.Ads.GoogleAds.V23.Services;
using Google.Ads.GoogleAds.V25.Resources;
class Uses
{
    void Build()
    {
        var google = GOOGLE_ADS_REPORT.campaign_name;
        var sa = SA360_REPORT_FIELDS.campaign_name;
        // GOOGLE_ADS_REPORT.ad_group_id
        /* SA360_REPORT_FIELDS.campaign_name */
        var query = "SELECT campaign.id, ad_group.id FROM campaign";
        var dynamicQuery = $"SELECT campaign.id FROM {resource}";
        Google.Ads.GoogleAds.V25.Services.GoogleAdsServiceClient client;
    }
}
