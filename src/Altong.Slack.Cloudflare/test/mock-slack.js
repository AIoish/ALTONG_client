const owner = {
  D_AB: { U_A: "U_B", U_B: "U_A", U_C: "U_D" },
  D_TEST: { U_SELF: "U_SENDER" }
};

export function tokenFor(team, user, suffix = "original") {
  return ["synthetic-access", team, user, suffix].join(".");
}

export function fakeSlack() {
  const revoked = new Set();
  return {
    revoked,
    async fetch(request) {
      const url = new URL(request.url);
      if (url.hostname !== "slack.com" || !url.pathname.startsWith("/api/")) return new Response(null, { status: 404 });
      const method = url.pathname.slice(5);
      const params = request.method === "GET" ? url.searchParams : new URLSearchParams(await request.text());
      const token = request.headers.get("authorization")?.replace(/^Bearer /, "") ?? "";
      const [, team, user] = token.split(".");
      const ok = data => Response.json({ ok: true, ...data });
      const fail = error => Response.json({ ok: false, error });

      if (method === "oauth.v2.access") {
        if (params.get("grant_type") === "refresh_token") {
          if (params.get("refresh_token") !== "synthetic-refresh") return fail("invalid_refresh_token");
          return ok({ access_token: tokenFor("T_A", "U_B", "refreshed"), refresh_token: "synthetic-refresh-next", token_type: "user", expires_in: 43200 });
        }
        const code = params.get("code") ?? "";
        if (!/^T_[A-Z]+\.U_[A-Z]+(?:\.[a-z0-9]+)?$/.test(code)) return fail("invalid_code");
        const [newTeam, newUser, suffix] = code.split(".");
        const grant = { id: newUser, token_type: "user", access_token: tokenFor(newTeam, newUser, suffix), scope: "im:history,im:read,users:read" };
        if (suffix === "rotating") Object.assign(grant, { expires_in: 130, refresh_token: "synthetic-refresh" });
        return ok({ app_id: "A_TEST", team: { id: newTeam }, access_token: "ignored-mock-bot-token", authed_user: grant });
      }
      if (method === "auth.test")
        return team && user && !revoked.has(token) ? ok({ team_id: team, user_id: user, user, team }) : fail("invalid_auth");
      if (method === "apps.event.authorizations.list") {
        if (token !== "synthetic-app-token") return fail("invalid_auth");
        const entries = (params.get("event_context") ?? "").split(",").map(value => {
          const [team_id, user_id] = value.split(":");
          return { team_id, user_id, is_bot: false };
        });
        const secondPage = params.get("cursor") === "rest";
        return ok({ authorizations: secondPage ? entries.slice(1) : entries.slice(0, 1),
          response_metadata: { next_cursor: !secondPage && entries.length > 1 ? "rest" : "" } });
      }
      if (method === "conversations.info") {
        const id = params.get("channel");
        const other = owner[id]?.[user];
        return other ? ok({ channel: { id, user: other, is_im: true, context_team_id: team } }) : fail("channel_not_found");
      }
      if (method === "users.info") return ok({ user: { profile: { display_name: "Synthetic sender" } } });
      if (method === "auth.revoke") {
        revoked.add(token);
        return ok({ revoked: true });
      }
      return fail("unknown_method");
    }
  };
}

export default fakeSlack();
