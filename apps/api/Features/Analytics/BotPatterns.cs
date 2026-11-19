// Bot user-agent fragments vendored from the isbot project
// (https://github.com/omrilotan/isbot), joined with '|' exactly as its
// index.js builds its pattern. Kept vendored so reports do not split when
// the upstream list moves on.
//
// # Unlicense
//
// This is free and unencumbered software released into the public domain.
//
// Anyone is free to copy, modify, publish, use, compile, sell, or
// distribute this software, either in source code form or as a compiled
// binary, for any purpose, commercial or non-commercial, and by any
// means.
//
// In jurisdictions that recognize copyright laws, the author or authors
// of this software dedicate any and all copyright interest in the
// software to the public domain. We make this dedication for the benefit
// of the public at large and to the detriment of our heirs and
// successors. We intend this dedication to be an overt act of
// relinquishment in perpetuity of all present and future rights to this
// software under copyright law.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
// IN NO EVENT SHALL THE AUTHORS BE LIABLE FOR ANY CLAIM, DAMAGES OR
// OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
// ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
//
// For more information, please refer to <http://unlicense.org/>

namespace Ari.Features.Analytics;

/// <summary>The joined isbot pattern, vendored. Unlicense, see header.</summary>
internal static class BotPatterns
{
    internal const string IsBot =
        " daum[ /]| deusu/|(?:^|[^g])news(?!sapphire)|(?<! channel/|google/)google(?!(?:wv|app|/google| pixel))|(?<! cu"
        + ")bots?(?:\\b|_)|(?<!cam)scan|(?<!lib)http|24x7|;\\s\\w+;$|@[a-z][\\w-]+\\.|\\(\\)|\\.com\\b|\\b\\w+/1\\.0;|\\b"
        + "bw/|\\bdlc\\b|\\bort/|\\bperl\\b|\\btime/|\\||^[<\\(;]|^[\\w \\.\\-\\(?:\\):%]+(?:/v?\\d+(?:\\.\\d+)?(?:\\.\\d"
        + "{1,10})*?)?(?:,|$)|^[\\w\\-]+/[\\w]+$|^[^ ]{50,}$|^\\d+\\b|^\\w*search\\b|^\\w+/[\\w\\(\\)]*$|^\\w+/\\d\\.\\d\\"
        + "s\\([\\w@]+\\)$|^active|^ad muncher|^amaya|^apache/|^avsdevicesdk/|^azure|^biglotron|^blackbox exporter|^bot|^"
        + "clamav[ /]|^claude-code/|^client/|^cobweb/|^custom|^ddg[_-]android|^discourse|^dispatch/\\d|^downcast/|^duckdu"
        + "ckgo|^email|^exodusmovement|^facebook|^getright/|^gozilla/|^hobbit|^hotzonu|^hwcdn/|^igetter/|^jeode/|^jetty/|"
        + "^jigsaw|^microsoft bits|^movabletype|^mozilla/\\d\\.\\d\\s[\\w\\.-]+$|^mozilla/\\d\\.\\d\\s\\((?:compatible;)?"
        + "(?:\\s?[\\w\\d-.]+[\\s/]\\d+\\.\\d+)?\\)$|^navermailapp|^netsurf|^offline|^openai/|^owler|^php|^postman|^ps_da"
        + "ily/|^python|^rank|^read|^reed|^remove\\.bg/|^rest|^rss|^snapchat|^sora |^space bison|^stape/|^svn|^swcd |^tar"
        + "inga|^thumbor/|^track|^w3c|^webbandit/|^webcopier|^wget|^whatsapp|^wordpress|^xenu link sleuth|^yahoo|^yandex|"
        + "^zdm/\\d|^zoom marketplace/|abuse|advisor|agent\\b|analyzer|archive|ask jeeves/teoma|attracta|audit|bluecoat d"
        + "rtr|browsex|burpcollaborator|capture|catch|check\\b|checker|chrome-lighthouse|chromeframe|classifier|cloudflar"
        + "e|collapsify\\b|convertify|cookiehubverify/|crawl|cursor/|cypress/|dareboost|datanyze|dejaclick|detect|discove"
        + "ry|dmbrowser|download|exaleadcloudview|feed|firephp|foregenix|functionize|grab|hardenize\\b|headless|hotjar|ht"
        + "track|hubspot marketing grader|ibisbrowser|infrawatch|insight|inspect|iplabel|java(?!;)|library|linkcheck|link"
        + "tiger|mail\\.ru/|manus-user/|marketgoo/|measure|monitor\\b|neustar wpm|node\\b|nutch|offbyone|openvas|optimize"
        + "|pageburst|pagespeed|parser|phantomjs|pingdom|playwright|powermarks|preview|productfinder|prospectingstudio|pr"
        + "oxy|ptst[ /]\\d|radar|readable/|retriever|rexx;|rigor|rss\\b|scrape|securityheaders|selenium|server|silktide|s"
        + "indup/|sogou|sparkler/|speedcurve|spider|splash|statuscake|supercleaner|synapse|synthetic|testlocally|tools|to"
        + "rrent|transcoder|url|validator|virtuoso|wappalyzer|watchtowr|webglance|webkit2png|whatcms/|xtate/";
}
