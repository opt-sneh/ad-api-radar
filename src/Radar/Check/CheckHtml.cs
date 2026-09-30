using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Radar;

public static class CheckHtml
{
    private static readonly Dictionary<string, (string Label, string Hint)> Meta = new()
    {
        ["BREAKING_RUNTIME"] = ("Breaking at runtime", "The API rejects this today."),
        ["BREAKING_COMPILE"] = ("Breaking at compile", "Won't compile against the target SDK."),
        ["UPCOMING_BREAK"] = ("Upcoming break", "Breaks when you move to the next version."),
        ["MIGRATION"] = ("Migration work", "SDK bump, namespace swap, regenerated code."),
        ["SILENT_RISK"] = (
            "Silent risk",
            "The call still succeeds, but results may change or go missing."
        ),
        ["RESILIENCE"] = ("Swallowed errors", "API failures hidden by catch blocks."),
        ["DEPRECATED"] = ("Deprecated", "Works now, scheduled for removal."),
        ["SUNSET"] = ("Sunset", "When your API version stops working."),
        ["COMPAT"] = ("Compatibility", "SDK and Google.Protobuf versions."),
        ["CLEANUP"] = ("Cleanup", "Unused definitions of removed fields."),
        ["UNKNOWN"] = ("Unknown", "Couldn't be classified."),
    };

    private const string Fonts =
        "<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\"><link rel=\"preconnect\" href=\"https://fonts.gstatic.com\" crossorigin>"
        + "<link rel=\"stylesheet\" href=\"https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500&family=IBM+Plex+Sans:wght@400;500;600;700&display=swap\">";

    // Layout: instrument-panel review. Top bar (brand + platform switch), hero with version track,
    // stat strip, scan pipeline, two-column triage (start here | to-scale category chart),
    // sticky filter bar, then findings grouped by category with severity pills.
    private const string Css = """
        :root{color-scheme:light;
        --bg:#f3f5f8;--panel:#ffffff;--raised:#f8f9fb;--ink:#141b25;--muted:#5b6778;--faint:#8a95a5;--line:#dde2ea;--soft:#edf0f4;
        --g-accent:#1a64d6;--g-soft:#e8f0fe;--m-accent:#0067b8;--m-soft:#e5f1fb;
        --high:#c0262d;--high-soft:#fdeceb;--med:#a55b00;--med-soft:#fdf2e1;--low:#556274;--low-soft:#edf0f4;--info:#3d6b8f;--info-soft:#e8f1f7;--ok:#177a3c;--ok-soft:#e5f4ea;
        --warn-line:#e7b660;--shadow:0 1px 2px rgba(20,27,37,.06),0 4px 14px rgba(20,27,37,.05);
        --font:"IBM Plex Sans",system-ui,-apple-system,"Segoe UI",Roboto,Arial,sans-serif;--mono:"IBM Plex Mono",ui-monospace,Consolas,Menlo,monospace}
        @media(prefers-color-scheme:dark){:root:not([data-theme="light"]){color-scheme:dark;
        --bg:#0e1319;--panel:#151c25;--raised:#1b2430;--ink:#e8edf3;--muted:#9fabba;--faint:#768396;--line:#2a3542;--soft:#202a36;
        --g-accent:#8ab4f8;--g-soft:#1d2d47;--m-accent:#5fb2f2;--m-soft:#16304a;
        --high:#ff8f8a;--high-soft:#3a1f22;--med:#f2b560;--med-soft:#3a2c17;--low:#a8b3c2;--low-soft:#232d39;--info:#8fc1e6;--info-soft:#1a2c3b;--ok:#7fd49c;--ok-soft:#173022;
        --warn-line:#8a6a2e;--shadow:0 1px 2px rgba(0,0,0,.3)}}
        :root[data-theme="dark"]{color-scheme:dark;
        --bg:#0e1319;--panel:#151c25;--raised:#1b2430;--ink:#e8edf3;--muted:#9fabba;--faint:#768396;--line:#2a3542;--soft:#202a36;
        --g-accent:#8ab4f8;--g-soft:#1d2d47;--m-accent:#5fb2f2;--m-soft:#16304a;
        --high:#ff8f8a;--high-soft:#3a1f22;--med:#f2b560;--med-soft:#3a2c17;--low:#a8b3c2;--low-soft:#232d39;--info:#8fc1e6;--info-soft:#1a2c3b;--ok:#7fd49c;--ok-soft:#173022;
        --warn-line:#8a6a2e;--shadow:0 1px 2px rgba(0,0,0,.3)}
        body.google{--accent:var(--g-accent);--accent-soft:var(--g-soft)}body.microsoft{--accent:var(--m-accent);--accent-soft:var(--m-soft)}
        *{box-sizing:border-box}html{scroll-behavior:smooth}
        body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.55 var(--font);-webkit-font-smoothing:antialiased}
        main{max-width:1160px;margin:0 auto;padding-inline:20px;padding-block:0 72px}
        h1,h2,h3{text-wrap:balance;margin:0}h1{font-size:clamp(26px,3.6vw,36px);line-height:1.15;font-weight:700;letter-spacing:-.02em}
        h2{font-size:17px;font-weight:600;letter-spacing:-.01em}p{margin:0}a{color:var(--accent)}code,.mono{font-family:var(--mono);font-size:.92em;overflow-wrap:anywhere}
        :focus-visible{outline:2px solid var(--accent);outline-offset:2px;border-radius:4px}
        .eyebrow{font:600 11.5px/1 var(--font);letter-spacing:.12em;text-transform:uppercase;color:var(--muted)}
        .top{display:flex;align-items:center;justify-content:space-between;gap:14px;flex-wrap:wrap;padding-block:18px}
        .brand{display:flex;align-items:center;gap:10px;color:var(--ink)}.brand b{display:block;font-size:15px;font-weight:700;letter-spacing:-.01em}.brand small{display:block;font-size:12px;color:var(--muted)}
        .radar{width:34px;height:34px;flex:none}.radar .sweep{transform-origin:20px 20px;animation:sweep 6s linear infinite}
        @keyframes sweep{to{transform:rotate(360deg)}}@media(prefers-reduced-motion:reduce){.radar .sweep{animation:none}html{scroll-behavior:auto}}
        .switch{display:flex;gap:4px;padding:4px;background:var(--panel);border:1px solid var(--line);border-radius:10px}
        .tab{display:flex;align-items:center;gap:8px;padding:6px 12px;border-radius:7px;font-size:13.5px;font-weight:500;color:var(--muted);white-space:nowrap}
        .tab.on{background:var(--accent-soft);color:var(--ink)}.tab a{color:inherit;text-decoration:none}.tab:has(a):hover{background:var(--soft);color:var(--ink)}
        .logo{width:18px;height:18px;flex:none}.logo.lg{width:44px;height:44px}
        .hero{background:var(--panel);border:1px solid var(--line);border-radius:14px;box-shadow:var(--shadow);padding:26px 28px;display:grid;grid-template-columns:minmax(0,1fr) auto;gap:22px;align-items:center}
        .hero-id{display:flex;gap:18px;align-items:center;min-width:0}.hero-id>div{min-width:0;display:grid;gap:6px}.sub{color:var(--muted);max-width:62ch}
        .track{display:grid;gap:8px;justify-items:end}.track-row{display:flex;align-items:center;gap:8px;flex-wrap:wrap;justify-content:flex-end}
        .ver{font:500 14px/1 var(--mono);padding:8px 11px;border-radius:8px;border:1px solid var(--line);background:var(--raised)}.ver.cur{border-color:var(--accent);background:var(--accent-soft)}
        .ver small{display:block;font:600 10px/1 var(--font);letter-spacing:.1em;text-transform:uppercase;color:var(--muted);margin-bottom:5px}
        .arrow{color:var(--faint)}.sunset{font-size:12.5px;color:var(--med)}
        .strip{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));margin-block:16px;background:var(--panel);border:1px solid var(--line);border-radius:12px;overflow:hidden}
        .stat{padding:16px 20px;display:grid;gap:3px;border-left:1px solid var(--line)}.stat:first-child{border-left:0}
        .stat strong{font-size:28px;line-height:1.1;font-weight:600;font-variant-numeric:tabular-nums;letter-spacing:-.02em}.stat span{font-size:13px;color:var(--muted)}
        .stat.hot strong{color:var(--high)}.stat .pill{justify-self:start;margin-top:4px}
        .coverage{display:grid;gap:10px;padding:14px 18px;border:1px solid var(--warn-line);background:var(--med-soft);border-radius:12px}
        .coverage strong{font-weight:600}.gaps{display:flex;flex-wrap:wrap;gap:6px;list-style:none;margin:0;padding:0}
        .gaps li{font-size:12.5px;padding:3px 9px;border-radius:999px;background:var(--panel);border:1px solid var(--line);color:var(--muted)}
        .pipeline{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:0;margin-block:16px 28px;list-style:none;padding:0}
        .pipeline li{position:relative;padding:10px 14px 10px 0;display:grid;gap:2px}.pipeline li+li{padding-left:22px}
        .pipeline li+li:before{content:"";position:absolute;left:4px;top:19px;width:10px;height:10px;border-top:2px solid var(--faint);border-right:2px solid var(--faint);transform:rotate(45deg)}
        .pipeline b{font:600 20px/1.1 var(--font);font-variant-numeric:tabular-nums}.pipeline span{font-size:12.5px;color:var(--muted)}
        .pipe-note{font-size:12.5px;color:var(--faint);margin-top:-18px;margin-bottom:28px}
        .triage{display:grid;grid-template-columns:minmax(0,1.15fr) minmax(0,1fr);gap:18px;align-items:start}
        .panel{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:18px 20px;min-width:0}.panel-head{margin-bottom:12px;display:flex;justify-content:space-between;align-items:baseline;gap:8px}
        .panel-head small{font-size:12.5px;color:var(--muted)}
        .priority{margin:0;padding:0;list-style:none;display:grid}.priority li{border-top:1px solid var(--soft)}.priority li:first-child{border-top:0}
        .priority a{display:grid;grid-template-columns:auto minmax(0,1fr);gap:4px 12px;padding:11px 6px;color:var(--ink);text-decoration:none;border-radius:8px}
        .priority a:hover{background:var(--raised)}.priority .pill{grid-row:span 2;align-self:start;margin-top:2px}
        .priority .name{font:500 14px/1.35 var(--mono);overflow-wrap:anywhere}.priority small{grid-column:2;color:var(--muted);font-size:12.5px;overflow-wrap:anywhere}
        .empty-note{color:var(--muted)}
        .cats{display:grid;gap:2px}.cat-row{display:grid;grid-template-columns:auto minmax(0,1fr) auto;gap:4px 10px;align-items:center;padding:8px 6px;border-radius:8px;cursor:pointer}
        .cat-row:hover{background:var(--raised)}.cat-row input{margin:0;accent-color:var(--accent);width:16px;height:16px}
        .cat-row .label{font-size:14px;font-weight:500}.cat-row .n{font:600 14px/1 var(--font);font-variant-numeric:tabular-nums;text-align:right}
        .cat-row .bar{grid-column:2/4;height:5px;border-radius:3px;background:var(--soft);overflow:hidden}.cat-row .bar i{display:block;height:100%;border-radius:3px;background:var(--c,var(--accent))}
        .cat-row .hint{grid-column:2/4;font-size:12px;color:var(--muted)}.cat-row:has(input:not(:checked)) .label,.cat-row:has(input:not(:checked)) .n{color:var(--faint)}
        .cat-row:has(input:not(:checked)) .bar i{opacity:.35}.panel-note{font-size:12px;color:var(--faint);margin-top:10px}
        .filters{position:sticky;top:env(safe-area-inset-top,0px);z-index:3;display:flex;flex-wrap:wrap;gap:10px 14px;align-items:center;margin-block:28px 18px;padding:10px 12px;background:var(--panel);border:1px solid var(--line);border-radius:12px;box-shadow:var(--shadow)}
        .filters input[type=search],.filters select{padding:7px 10px;border:1px solid var(--line);border-radius:8px;font:inherit;font-size:14px;background:var(--raised);color:var(--ink)}
        .filters input[type=search]{flex:1 1 240px;min-width:0}.filters label{font-size:13px;color:var(--muted);display:flex;align-items:center;gap:6px}
        .filters button{font:inherit;font-size:13px;padding:6px 10px;border-radius:8px;border:1px solid var(--line);background:var(--raised);color:var(--ink);cursor:pointer}.filters button:hover{border-color:var(--accent)}
        .filters .count{margin-left:auto;font-size:13px;color:var(--muted);font-variant-numeric:tabular-nums}
        #empty{display:none;color:var(--muted);padding:24px;text-align:center;border:1px dashed var(--line);border-radius:12px}
        .cat{scroll-margin-top:80px;margin-top:30px}.cat-head{display:flex;align-items:baseline;gap:10px;flex-wrap:wrap;margin-bottom:10px}
        .cat-head h2{font-size:18px}.cat-head .tally{font:600 13px/1 var(--font);padding:3px 8px;border-radius:999px;background:var(--soft);color:var(--muted)}
        .cat-head p{flex-basis:100%;font-size:13px;color:var(--muted)}.cat-head code{font-size:11.5px;color:var(--faint)}
        details.group{margin-block:8px;background:var(--panel);border:1px solid var(--line);border-radius:10px;overflow:hidden}
        details.group>summary{cursor:pointer;list-style:none;padding:13px 16px;display:grid;grid-template-columns:minmax(0,1fr) auto;gap:4px 14px}
        details.group>summary::-webkit-details-marker{display:none}details.group>summary:hover{background:var(--raised)}
        details.group>summary:after{content:"";grid-column:2;grid-row:1;width:8px;height:8px;margin:6px 4px 0 0;border-right:2px solid var(--faint);border-bottom:2px solid var(--faint);transform:rotate(45deg);transition:transform .15s}
        details.group[open]>summary:after{transform:rotate(-135deg);margin-top:10px}details.group[open]>summary{border-bottom:1px solid var(--soft)}
        .path{font:500 14.5px/1.35 var(--mono);overflow-wrap:anywhere}.summary-detail{grid-column:1;color:var(--muted);font-size:13.5px}
        .meta{grid-column:1;display:flex;flex-wrap:wrap;gap:6px;align-items:center;font-size:12.5px;color:var(--muted);margin-top:4px}
        .pill{display:inline-flex;align-items:center;gap:5px;font:600 11px/1 var(--font);letter-spacing:.04em;text-transform:uppercase;padding:4px 8px;border-radius:999px;white-space:nowrap}
        .pill:before{content:"";width:6px;height:6px;border-radius:50%;background:currentColor}
        .sev-high{color:var(--high);background:var(--high-soft)}.sev-medium{color:var(--med);background:var(--med-soft)}.sev-low{color:var(--low);background:var(--low-soft)}.sev-info,.sev-unknown{color:var(--info);background:var(--info-soft)}
        .pill.warn{color:var(--med);background:var(--med-soft)}.tag{font-size:12px;padding:2px 7px;border-radius:6px;background:var(--soft);color:var(--muted)}
        .desc{padding:14px 16px;border-bottom:1px solid var(--soft);display:grid;gap:8px;max-width:88ch}.desc strong{font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:var(--muted)}
        .next{font-size:13.5px;padding:8px 12px;border-radius:8px;background:var(--accent-soft)}.next b{font-weight:600}
        article{padding:12px 16px;border-bottom:1px solid var(--soft);display:grid;gap:5px}article:last-child{border-bottom:0}
        .loc{font:500 13px/1.4 var(--mono);overflow-wrap:anywhere}.info{font-size:12.5px;color:var(--muted);display:flex;flex-wrap:wrap;gap:4px 10px}
        .handled{color:var(--ok)}.fix{color:var(--ok);font-weight:500}
        article details summary{cursor:pointer;font-size:12.5px;color:var(--accent);width:max-content}
        article ul{margin:8px 0 0;padding:10px 12px 10px 28px;font-size:12.5px;background:var(--raised);border-radius:8px;display:grid;gap:4px}
        .technical{margin-top:40px;background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:16px 20px}
        .technical>summary{cursor:pointer;font-weight:600}.technical h2{margin-top:18px;margin-bottom:6px;font-size:15px}
        ul.notes{padding-left:18px;margin:0;display:grid;gap:5px}ul.notes li{color:var(--muted);font-size:13.5px}
        footer{margin-top:26px;font-size:12px;color:var(--faint);display:flex;gap:8px;align-items:center}
        @media(max-width:860px){.hero{grid-template-columns:1fr}.track{justify-items:start}.track-row{justify-content:flex-start}.triage{grid-template-columns:1fr}}
        @media(max-width:640px){main{padding-inline:16px}.hero{padding:20px}.logo.lg{width:36px;height:36px}.strip,.pipeline{grid-template-columns:repeat(2,minmax(0,1fr))}
        .stat:nth-child(3){border-left:0}.stat:nth-child(n+3){border-top:1px solid var(--line)}.pipeline li:nth-child(3){padding-left:0}.pipeline li:nth-child(3):before{display:none}
        .filters{position:static}.filters .count{margin-left:0}.switch{flex-wrap:wrap}.tab{white-space:normal}.track-row .ver{flex:none}}
        @media print{body{background:#fff}.filters,.switch{display:none}details.group,.hero,.strip{break-inside:avoid}details.group:not([open])>*{display:block}}
        """;

    private const string Script = """
        (()=>{const $=s=>document.querySelector(s),$$=s=>[...document.querySelectorAll(s)];
        const apply=()=>{const on=new Set($$('[data-filter-category]:checked').map(x=>x.dataset.filterCategory));
        const q=$('#q').value.trim().toLowerCase(),sev=$('#severity').value,conf=$('#confidence').value,hide=$('#hide-handled').checked;let shown=0;
        for(const a of $$('article[data-category]')){const h=!on.has(a.dataset.category)||(sev!=='all'&&sev!==a.dataset.severity)||(conf!=='all'&&conf!==a.dataset.confidence)||(hide&&a.dataset.handled==='true')||(q&&!a.dataset.text.includes(q));a.hidden=h;if(!h)shown++;}
        for(const d of $$('details.group'))d.hidden=!d.querySelector('article:not([hidden])');
        for(const s of $$('section.cat'))s.hidden=!s.querySelector('details.group:not([hidden])');
        $('#count').textContent=shown+(shown===1?' location shown':' locations shown');$('#empty').style.display=shown?'none':'block';};
        const all=$('#toggle-all');all.addEventListener('click',()=>{const open=all.dataset.open!=='true';
        for(const d of $$('details.group:not([hidden])'))d.open=open;all.dataset.open=String(open);all.textContent=open?'Collapse all':'Expand all';});
        for(const a of $$('a[href^="#finding-"]'))a.addEventListener('click',()=>{const d=document.getElementById(a.getAttribute('href').slice(1));if(d)d.open=true;});
        document.addEventListener('input',apply);document.addEventListener('change',apply);apply();})();
        """;

    // Platform marks, drawn as simple inline SVG so the report stays a single offline file.
    private const string RadarMark =
        "<svg class=\"radar\" viewBox=\"0 0 40 40\" aria-hidden=\"true\"><circle cx=\"20\" cy=\"20\" r=\"18\" fill=\"var(--accent-soft)\" stroke=\"var(--accent)\" stroke-width=\"1.5\"/>"
        + "<circle cx=\"20\" cy=\"20\" r=\"11.5\" fill=\"none\" stroke=\"var(--accent)\" stroke-opacity=\".45\"/><circle cx=\"20\" cy=\"20\" r=\"5\" fill=\"none\" stroke=\"var(--accent)\" stroke-opacity=\".45\"/>"
        + "<g class=\"sweep\"><path d=\"M20 20 L20 2 A18 18 0 0 1 35.6 11 Z\" fill=\"var(--accent)\" fill-opacity=\".28\"/><line x1=\"20\" y1=\"20\" x2=\"20\" y2=\"2\" stroke=\"var(--accent)\" stroke-width=\"1.5\"/></g>"
        + "<circle cx=\"27.5\" cy=\"12\" r=\"2.2\" fill=\"var(--high)\"/><circle cx=\"13\" cy=\"26\" r=\"1.8\" fill=\"var(--med)\"/></svg>";

    private static string GoogleAdsLogo(string css) =>
        $"<svg class=\"logo {css}\" viewBox=\"0 0 48 48\" aria-hidden=\"true\">"
        + "<line x1=\"14\" y1=\"37\" x2=\"26\" y2=\"11\" stroke=\"#fbbc04\" stroke-width=\"11\" stroke-linecap=\"round\"/>"
        + "<line x1=\"26\" y1=\"11\" x2=\"37\" y2=\"36\" stroke=\"#4285f4\" stroke-width=\"11\" stroke-linecap=\"round\"/>"
        + "<circle cx=\"13\" cy=\"36.5\" r=\"6\" fill=\"#34a853\"/></svg>";

    private static string MicrosoftLogo(string css) =>
        $"<svg class=\"logo {css}\" viewBox=\"0 0 48 48\" aria-hidden=\"true\">"
        + "<rect x=\"3\" y=\"3\" width=\"20\" height=\"20\" fill=\"#f25022\"/><rect x=\"25\" y=\"3\" width=\"20\" height=\"20\" fill=\"#7fba00\"/>"
        + "<rect x=\"3\" y=\"25\" width=\"20\" height=\"20\" fill=\"#00a4ef\"/><rect x=\"25\" y=\"25\" width=\"20\" height=\"20\" fill=\"#ffb900\"/></svg>";

    // Bar colour per category, from the severity tokens.
    private static string CategoryColor(string category) => category switch
    {
        "BREAKING_RUNTIME" or "BREAKING_COMPILE" or "UPCOMING_BREAK" => "var(--high)",
        "SILENT_RISK" or "RESILIENCE" or "DEPRECATED" or "SUNSET" => "var(--med)",
        "MIGRATION" or "COMPAT" => "var(--accent)",
        _ => "var(--low)",
    };

    public static string Render(CheckReport report, string? otherReportHref = null)
    {
        var html = new StringBuilder();
        int Funnel(string key) => report.Funnel.GetValueOrDefault(key);
        bool microsoft = report.Platform.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        string platform = microsoft ? "Microsoft Advertising" : "Google Ads";
        string other = microsoft ? "Google Ads" : "Microsoft Advertising";
        string platformClass = microsoft ? "microsoft" : "google";
        string Logo(bool isMicrosoft, string css = "") => isMicrosoft ? MicrosoftLogo(css) : GoogleAdsLogo(css);
        (string Label, string Hint) InfoFor(string category) => microsoft && category == "COMPAT"
            ? ("Compatibility", "Microsoft SDK package dependencies.") : Info(category);
        int actionable = report.Findings.Count(ReportFindings.IsActionable);
        int high = report.Findings.Count(f => ReportFindings.IsActionable(f) && f.Severity == "high");
        int files = report.Findings.Where(f => f.Line > 0 && ReportFindings.IsActionable(f))
            .Select(f => f.File).Distinct(StringComparer.Ordinal).Count();

        html.Append("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>").Append(E(platform)).Append(" upgrade review · Ad API Radar</title>")
            .Append(Fonts).Append("<style>").Append(Css).Append("</style><body class=\"").Append(platformClass).Append("\"><main>");

        // Top bar: brand and platform switch.
        html.Append("<div class=\"top\"><div class=\"brand\">").Append(RadarMark)
            .Append("<span><b>Ad API Radar</b><small>API upgrade review for Optmyzr</small></span></div>")
            .Append("<nav class=\"switch\" aria-label=\"Platform\"><span class=\"tab on\" aria-current=\"page\">")
            .Append(Logo(microsoft)).Append(E(platform)).Append("</span>");
        if (otherReportHref is not null)
            html.Append("<span class=\"tab\">").Append(Logo(!microsoft))
                .Append("<a href=\"").Append(E(otherReportHref)).Append("\">Open ").Append(other).Append(" report</a></span>");
        html.Append("</nav></div>");

        // Hero with the version track.
        var sunset = report.Findings.FirstOrDefault(f => f.Category == "SUNSET" && f.Severity == "high");
        html.Append("<header class=\"hero\"><div class=\"hero-id\">").Append(Logo(microsoft, "lg"))
            .Append("<div><span class=\"eyebrow\">").Append(E(platform)).Append(" · compiled code scan</span><h1>")
            .Append(E(platform)).Append(" upgrade review</h1><p class=\"sub\">Changes found in compiled Optmyzr code and API sources.</p></div></div>")
            .Append("<div class=\"track\"><div class=\"track-row\"><span class=\"ver cur\"><small>Current</small>").Append(E(report.Target)).Append("</span>");
        if (report.Next.Count > 0)
            foreach (string next in report.Next)
                html.Append("<span class=\"arrow\" aria-hidden=\"true\">→</span><span class=\"ver\"><small>Checking</small>").Append(E(next)).Append("</span>");
        else
            html.Append("<span class=\"ver\"><small>Status</small>latest</span>");
        html.Append("</div>");
        if (report.Next.Count == 0)
            html.Append("<span class=\"sunset\">No newer version in this scan</span>");
        if (sunset is not null)
            html.Append("<span class=\"sunset\">").Append(E(Preview(sunset.Message))).Append("</span>");
        html.Append("</div></header>");

        // Stat strip.
        html.Append("<section class=\"strip\" aria-label=\"Report at a glance\">")
            .Append("<div class=\"stat\"><strong>").Append(actionable).Append("</strong><span>Findings to review</span></div>")
            .Append("<div class=\"stat").Append(high > 0 ? " hot" : "").Append("\"><strong>").Append(high).Append("</strong><span>High priority</span></div>")
            .Append("<div class=\"stat\"><strong>").Append(files).Append("</strong><span>Files with review findings</span></div>")
            .Append("<div class=\"stat\"><span>Scan coverage</span><span class=\"pill warn\">Partial</span></div></section>");

        var gaps = new List<string>();
        if (report.ParseErrorFiles > 0) gaps.Add($"{report.ParseErrorFiles} files failed to parse");
        if (report.InterpolatedQueries > 0) gaps.Add($"{report.InterpolatedQueries} dynamic queries not statically checked");
        if (report.Limitations.Any(l => l.StartsWith("STALE SOURCE", StringComparison.Ordinal))) gaps.Add("upstream sources are stale");
        if (!microsoft && !report.Limitations.Any(l => l.StartsWith("Live GAQL validation", StringComparison.Ordinal)))
            gaps.Add("live GAQL validation not run");
        gaps.Add("changed or empty API data needs a separate behaviour check");
        html.Append("<div class=\"coverage\" role=\"note\"><strong>Coverage is partial. An empty finding list is not an all-clear.</strong><ul class=\"gaps\">");
        foreach (string gap in gaps)
            html.Append("<li>").Append(E(gap)).Append("</li>");
        html.Append("</ul></div>");

        // Scan pipeline: upstream changes narrowed down to files to open.
        if (Funnel("changesInCatalog") > 0)
            html.Append("<ol class=\"pipeline\" aria-label=\"Scan pipeline\">")
                .Append("<li><b>").Append(Funnel("changesInCatalog")).Append("</b><span>upstream changes</span></li>")
                .Append("<li><b>").Append(Funnel("changesTouchingCode")).Append("</b><span>touch Optmyzr code</span></li>")
                .Append("<li><b>").Append(Funnel("actionable")).Append("</b><span>actionable findings</span></li>")
                .Append("<li><b>").Append(Funnel("files")).Append("</b><span>files</span></li></ol>");
        else
            html.Append("<ol class=\"pipeline\" aria-label=\"Scan pipeline\">")
                .Append("<li><b>0</b><span>newer-version changes</span></li>")
                .Append("<li><b>").Append(Funnel("findings")).Append("</b><span>findings on current code</span></li>")
                .Append("<li><b>").Append(Funnel("actionable")).Append("</b><span>actionable findings</span></li>")
                .Append("<li><b>").Append(Funnel("files")).Append("</b><span>files</span></li></ol>");
        html.Append("<p class=\"pipe-note\">");
        if (Funnel("changesInCatalog") > 0)
            html.Append(Funnel("changesInCatalog")).Append(" upstream changes → ").Append(Funnel("changesTouchingCode"))
                .Append(" touch Optmyzr code → ").Append(Funnel("actionable")).Append(" actionable findings in ")
                .Append(Funnel("files")).Append(" files. ");
        html.Append("Counts describe scanner output, not confirmed defects.</p>");

        // Triage: start here + category chart.
        var priorities = report.Findings.Where(ReportFindings.IsActionable)
            .OrderBy(f => ReportFindings.SeverityOrder(f.Severity))
            .ThenBy(f => ReportFindings.CategoryOrder(f.Category))
            .GroupBy(ReportFindings.GroupKey)
            .Select(group => group.First()).Take(5).ToList();
        html.Append("<div class=\"triage\"><section class=\"panel\"><div class=\"panel-head\"><h2>Start here</h2><small>Top ").Append(priorities.Count).Append(" by priority</small></div>");
        if (priorities.Count == 0)
            html.Append("<p class=\"empty-note\">No findings are currently marked for action. Review scan coverage and the remaining categories below.</p>");
        else
        {
            html.Append("<ol class=\"priority\">");
            foreach (var finding in priorities)
            {
                var (label, _) = InfoFor(finding.Category);
                html.Append("<li><a href=\"#finding-").Append(GroupAnchor(finding)).Append("\">")
                    .Append(Pill(finding.Severity)).Append("<span class=\"name\">").Append(E(finding.Path)).Append("</span><small>")
                    .Append(E(label)).Append(" · ").Append(E(Short(finding.File))).Append("</small></a></li>");
            }
            html.Append("</ol>");
        }
        html.Append("</section>");

        var categories = ReportFindings
            .Categories.Concat(report.Counts.Keys)
            .Concat(report.Findings.Select(f => f.Category))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(ReportFindings.CategoryOrder)
            .ToList();
        int Count(string category) => report.Counts.GetValueOrDefault(category, report.Findings.Count(f => f.Category == category));
        int max = Math.Max(1, categories.Select(Count).DefaultIfEmpty(0).Max());
        html.Append("<section class=\"panel\"><div class=\"panel-head\"><h2>Summary counts</h2><small>Tick to filter</small></div><div class=\"cats\">");
        foreach (string category in categories)
        {
            int count = Count(category);
            if (count == 0) continue;
            var (label, hint) = InfoFor(category);
            html.Append("<label class=\"cat-row\" style=\"--c:").Append(CategoryColor(category)).Append("\"><input type=\"checkbox\" data-filter-category=\"")
                .Append(E(category)).Append('"').Append(category == "CLEANUP" ? "" : " checked")
                .Append(" aria-label=\"Show ").Append(E(label)).Append("\"><span class=\"label\">").Append(E(label))
                .Append("</span><span class=\"n\">").Append(count).Append("</span><span class=\"bar\"><i style=\"width:")
                .Append(Math.Max(2, 100 * count / max)).Append("%\"></i></span><span class=\"hint\">").Append(E(hint)).Append("</span></label>");
        }
        html.Append("</div><p class=\"panel-note\">Cleanup starts hidden; tick it to review unused definitions.</p></section></div>");

        // Filters.
        html.Append("<div class=\"filters\"><input type=\"search\" id=\"q\" placeholder=\"Search field, file or message\" aria-label=\"Search findings\">")
            .Append("<label>Severity <select id=\"severity\"><option value=\"all\">All</option><option>high</option><option>medium</option><option>low</option><option>info</option></select></label>")
            .Append("<label>Confidence <select id=\"confidence\"><option value=\"all\">All</option><option>high</option><option>medium</option><option>low</option></select></label>")
            .Append("<label><input type=\"checkbox\" id=\"hide-handled\"> Hide possibly handled</label>")
            .Append("<button type=\"button\" id=\"toggle-all\">Expand all</button>")
            .Append("<span class=\"count\" id=\"count\" aria-live=\"polite\"></span></div><p id=\"empty\">No findings match these filters.</p>");

        // Findings grouped by category.
        var groups = report
            .Findings.OrderBy(f => ReportFindings.CategoryOrder(f.Category))
            .ThenBy(f => ReportFindings.SeverityOrder(f.Severity))
            .ThenByDescending(f => f.UseCount)
            .GroupBy(ReportFindings.GroupKey)
            .OrderBy(g => ReportFindings.CategoryOrder(g.First().Category))
            .ThenBy(g => ReportFindings.SeverityOrder(g.First().Severity))
            .ThenByDescending(g => g.Count());
        string? heading = null;
        foreach (var group in groups)
        {
            var first = group.First();
            if (heading != first.Category)
            {
                if (heading is not null)
                    html.Append("</section>");
                heading = first.Category;
                var (label, hint) = InfoFor(heading);
                html.Append("<section class=\"cat\" id=\"cat-").Append(E(heading.ToLowerInvariant())).Append("\"><div class=\"cat-head\"><h2>")
                    .Append(E(label)).Append("</h2><span class=\"tally\">").Append(Count(heading))
                    .Append("</span><p>").Append(E(hint)).Append(" <code>").Append(E(heading)).Append("</code></p></div>");
            }
            int locations = group.Sum(f =>
                f.Lines.Count > 0 ? f.Lines.Count
                : f.Line > 0 ? 1
                : 0
            );
            bool sameMessage = group.Select(f => f.Message).Distinct(StringComparer.Ordinal).Count() == 1;
            html.Append("<details class=\"group\" id=\"finding-").Append(GroupAnchor(first))
                .Append("\"><summary><span class=\"path\">").Append(E(first.Path))
                .Append("</span><span class=\"summary-detail\">").Append(E(Preview(first.Message)))
                .Append("</span><span class=\"meta\">").Append(Pill(first.Severity))
                .Append("<span class=\"tag\">").Append(E(first.Confidence)).Append(" confidence</span><span>")
                .Append(locations).Append(locations == 1 ? " location" : " locations").Append("</span></span></summary>");
            if (sameMessage)
                html.Append("<div class=\"desc\"><strong>Why it matters</strong><p>").Append(E(first.Message))
                    .Append("</p><p class=\"next\"><b>Next check:</b> ").Append(E(Guidance(first.Category))).Append("</p></div>");
            foreach (var finding in group)
                RenderFinding(finding, !sameMessage);
            html.Append("</details>");
        }
        if (heading is not null)
            html.Append("</section>");

        html.Append("<details class=\"technical\"><summary>Sources, coverage and technical notes</summary>");
        if (report.Announcements.Count > 0)
        {
            html.Append("<h2>Recent announcements to review</h2><ul class=\"notes\">");
            foreach (var announcement in report.Announcements)
                html.Append("<li>").Append(E(announcement)).Append("</li>");
            html.Append("</ul>");
        }
        html.Append("<h2>Not checked / unknown</h2><ul class=\"notes\">");
        if (report.Platform == "Google Ads")
        {
            Note("Non-GAQL queries skipped", report.NonGaqlQueriesSkipped);
            Note("Unknown-root paths skipped", report.UnknownRootPathsSkipped);
            Note("Behaviour rows without hits", report.BehaviourRowsWithoutHits);
            Note("Interpolated queries", report.InterpolatedQueries);
            Note("Parse-error files", report.ParseErrorFiles);
        }
        foreach (var (owner, count) in report.UnknownDefinitionsByOwner.OrderBy(p => p.Key))
            Note("Unknown-platform definitions, " + owner, count);
        foreach (var limitation in report.Limitations)
            html.Append("<li>").Append(E(limitation)).Append("</li>");
        html.Append("</ul></details><footer>").Append(RadarMark.Replace("class=\"radar\"", "class=\"radar\" style=\"width:18px;height:18px\""))
            .Append("Generated by Ad API Radar. Read-only scan; nothing was changed in the repository.</footer></main><script>")
            .Append(Script).Append("</script></body></html>");
        return html.ToString();

        void Note(string label, int value) =>
            html.Append("<li>").Append(E(label)).Append(": ").Append(value).Append("</li>");
        void RenderFinding(Finding finding, bool showMessage)
        {
            html.Append("<article data-category=\"").Append(E(finding.Category))
                .Append("\" data-severity=\"").Append(E(finding.Severity))
                .Append("\" data-confidence=\"").Append(E(finding.Confidence))
                .Append("\" data-handled=\"").Append(finding.PossiblyHandled ? "true" : "false")
                .Append("\" data-text=\"").Append(E((finding.Path + " " + finding.File + " " + finding.Message).ToLowerInvariant()))
                .Append("\"><div class=\"loc\" title=\"").Append(E(finding.File)).Append("\">")
                .Append(E(Short(finding.File)))
                .Append(finding.Line > 0 ? ":" + finding.Line : "")
                .Append(finding.Lines.Count > 1 ? E(" (lines " + string.Join(", ", finding.Lines) + ")") : "")
                .Append("</div>");
            if (showMessage)
                html.Append("<p>").Append(E(finding.Message)).Append("</p>");
            var info = new List<string>();
            if (finding.UseCount > 0)
                info.Add(finding.UseCount + (finding.UseCount == 1 ? " use" : " uses"));
            info.Add(E(finding.Platform));
            if (finding.PossiblyHandled)
                info.Add("<span class=\"handled\">possibly handled</span>");
            if (finding.SuggestedReplacement is not null)
                info.Add("<span class=\"fix\">replace with " + E(finding.SuggestedReplacement) + "</span>");
            html.Append("<div class=\"info\">").Append(string.Join("<span aria-hidden=\"true\">·</span>", info)).Append("</div>");
            if (finding.Evidence.Count > 0)
            {
                html.Append("<details><summary>Evidence (").Append(finding.Evidence.Count).Append(")</summary><ul>");
                foreach (var evidence in finding.Evidence)
                    html.Append("<li><code>").Append(E(evidence)).Append("</code></li>");
                html.Append("</ul></details>");
            }
            html.Append("</article>");
        }
    }

    private static (string Label, string Hint) Info(string category) =>
        Meta.TryGetValue(category, out var info) ? info : (category, "");

    private static string Pill(string severity) =>
        $"<span class=\"pill sev-{E(severity.ToLowerInvariant())}\">{E(severity)}</span>";

    private static string Preview(string message)
    {
        string text = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 175 ? text : text[..172].TrimEnd() + "…";
    }

    private static string GroupAnchor(Finding finding) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ReportFindings.GroupKey(finding))))[..12];

    private static string Guidance(string category) => category switch
    {
        "BREAKING_COMPILE" => "Build the affected project with the target SDK and fix the incompatible reference.",
        "BREAKING_RUNTIME" => "Confirm the affected call or query, then change it and validate again.",
        "UPCOMING_BREAK" => "Review this location before adopting the next API or SDK version.",
        "SILENT_RISK" => "Check the returned data or affected workflow with a representative fixture.",
        "RESILIENCE" => "Check whether this catch block reports API failures to callers or monitoring.",
        "DEPRECATED" or "SUNSET" => "Confirm the deadline and schedule the migration.",
        "COMPAT" or "MIGRATION" => "Review package versions and build the affected project.",
        "CLEANUP" => "Confirm the definition has no compiled use before removing it.",
        _ => "Inspect the evidence and decide whether this applies to the affected code.",
    };

    private static string Short(string file)
    {
        const string prefix = "code/backend/OptmyzrProcessorService/";
        return file.StartsWith(prefix, StringComparison.Ordinal) ? file[prefix.Length..] : file;
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
