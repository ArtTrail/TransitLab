// Fires .github/workflows/mobs-sync.yml via workflow_dispatch on Cloudflare's Cron Trigger
// schedule (see wrangler.toml) -- GitHub Actions' own cron for that workflow proved unreliable.
// This Worker does nothing else: no R2 access, no sync logic. GITHUB_PAT is a fine-grained
// token scoped only to ArtTrail/TransitLab with Actions:write, set via `wrangler secret put`.

const DISPATCH_URL =
  "https://api.github.com/repos/ArtTrail/TransitLab/actions/workflows/mobs-sync.yml/dispatches";

async function triggerSync(env) {
  const resp = await fetch(DISPATCH_URL, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${env.GITHUB_PAT}`,
      Accept: "application/vnd.github+json",
      "X-GitHub-Api-Version": "2022-11-28",
      "User-Agent": "TransitLab-MObsSync-Trigger",
    },
    body: JSON.stringify({ ref: "main" }),
  });

  if (!resp.ok) {
    const detail = await resp.text();
    throw new Error(`GitHub workflow_dispatch failed: ${resp.status} ${detail}`);
  }
}

export default {
  async scheduled(event, env, ctx) {
    ctx.waitUntil(triggerSync(env));
  },

  // Manual trigger for testing, e.g. curl https://<worker-url>/trigger -X POST
  async fetch(request, env) {
    if (request.method !== "POST" || new URL(request.url).pathname !== "/trigger") {
      return new Response("POST /trigger to manually fire the MObs sync workflow.", { status: 200 });
    }
    try {
      await triggerSync(env);
      return new Response("Triggered.", { status: 200 });
    } catch (err) {
      return new Response(String(err), { status: 500 });
    }
  },
};
