using System.Text.Json;
using JustWatch.Core.Services;

internal static class WebServiceDetectorTests
{
    public static void Run(string root)
    {
        var directory = Path.Combine(root, "classification"); Directory.CreateDirectory(directory);
        void Source(string name, string content)
        {
            var path = Path.Combine(directory, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
        }
        Source("http.mjs", "import http from 'node:http'; http.createServer((q,r)=>r.end('hi')).listen(3000);");
        Source("src/server.ts", "Bun.serve({port:3000,fetch:()=>new Response('hi')});");
        Source("dispatcher.ts", "import { serve } from './src/server.ts'; if (process.argv[2] === 'serve') serve();");
        Source("tcp.js", "import net from 'node:net'; net.createServer(()=>{}).listen(3000);");
        Source("cli.js", "console.log('command line app');");
        Source("comment.js", "// Bun.serve({});\n/* import http from 'node:http'; http.createServer().listen(3000); */\nconsole.log('hi');");
        Source("factory.mjs", "import http from 'node:http'; export const create = () => http.createServer(()=>{});");
        Source("composed.mjs", "import { create } from './factory.mjs'; create().listen(3000);");
        Source("scripts/wrapper.mjs", "import { spawn } from 'node:child_process'; spawn(process.execPath, ['--import','tsx','src/server.ts']);");
        Source("child/package.json", "{\"scripts\":{\"dev\":\"vite\"}}");
        var cases = new (string Command, bool Expected)[]
        {
            ("vite", true), ("vite --host 0.0.0.0", true), ("vite build", false),
            ("vite preview", true), ("vite --help", false), ("vitepress build docs", false),
            ("next dev --turbopack", true), ("next start", true), ("next build", false),
            ("astro dev", true), ("astro preview", true), ("astro check", false),
            ("cross-env NODE_ENV=development next dev", true), ("npx --yes next dev", true),
            ("bunx vite", true), ("pnpm exec vite", true), ("npm exec -- vite", true),
            ("react-scripts start", true), ("vue-cli-service serve", true), ("ng serve", true),
            ("webpack serve", true), ("webpack --watch", false), ("tsup --watch", false),
            ("tsc -w", false), ("wxt", false), ("wxt -b firefox", false),
            ("expo start", false), ("expo start --android", false), ("expo start --ios", false),
            ("expo start --web", true), ("expo start --web --ios", false),
            ("wrangler pages dev dist", true), ("wrangler deploy", false),
            ("node http.mjs", true), ("node cli.js", false), ("node tcp.js", false),
            ("node comment.js", false), ("node --import http.mjs cli.js", false),
            ("node missing.js", false), ("bun --watch run src/server.ts", true),
            ("bun run dispatcher.ts serve", true), ("bun run dispatcher.ts accounts", false),
            ("bun run dispatcher.ts --help", false), ("node composed.mjs", true),
            ("node scripts/wrapper.mjs", true), ("node dist/server.js", true),
            ("nodemon --exec tsx src/server.ts", true), ("tsx src/terminal.tsx", false),
            ("npm run web", true), ("yarn web", true), ("bun run web", true),
            ("npm run compiler", false), ("npm run cycle", false),
            ("npm run dev --prefix child", true), ("cd child && npm run dev", true),
            ("npm run dev --prefix ..", false), ("turbo dev", false),
            ("pnpm --filter site dev", false), ("node cli.js && vite", true),
            ("node --import ./env.mjs ./node_modules/wrangler/bin/wrangler.js dev", true)
        };
        foreach (var (command, expected) in cases)
        {
            using var package = JsonDocument.Parse(JsonSerializer.Serialize(new { scripts = new Dictionary<string,string>
            { ["dev"] = command, ["web"] = "vite", ["compiler"] = "tsup --watch", ["cycle"] = "npm run cycle2", ["cycle2"] = "npm run cycle" } }));
            var actual = new WebServiceDetector(directory, package.RootElement).IsWebScript("dev");
            if (actual != expected) throw new Exception($"Web service classification: {command}, expected {expected}, got {actual}");
        }
        using var lifecycle = JsonDocument.Parse("{\"scripts\":{\"prestart\":\"vite\",\"postdev\":\"vite\",\"build\":\"vite\",\"test:web\":\"vite\",\"smoke:layout\":\"node http.mjs\",\"e2e\":\"node http.mjs\",\"preview\":\"vite preview\"}}");
        var detector = new WebServiceDetector(directory, lifecycle.RootElement);
        if (new[] { "prestart", "postdev", "build", "test:web", "smoke:layout", "e2e" }.Any(detector.IsWebScript) || !detector.IsWebScript("preview")) throw new Exception("Lifecycle and smoke scripts must stay hidden; preview is a web service");
        using var cli = JsonDocument.Parse("{\"bin\":\"dispatcher.ts\",\"scripts\":{\"start\":\"bun dispatcher.ts\",\"serve\":\"bun dispatcher.ts serve\"}}");
        var cliDetector = new WebServiceDetector(directory, cli.RootElement);
        if (cliDetector.IsWebScript("start") || !cliDetector.IsWebScript("serve")) throw new Exception("An exported CLI needs an explicit server mode");
        Console.WriteLine($"PASS {cases.Length + 9} web-service classification cases: frameworks, aliases, HTTP entrypoints, CLI/build/mobile exclusions");
    }
}
