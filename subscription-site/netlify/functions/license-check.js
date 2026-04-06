const Stripe = require("stripe");

/**
 * POST body JSON: { customerId, deviceId }
 * Env: STRIPE_SECRET_KEY
 *
 * Parrainage / mois gratuits : sur le client Stripe, renseigner metadata.free_access_until (ISO 8601 UTC).
 * Le serveur prend le max entre fin de période d'abonnement et free_access_until.
 * Jusqu'à 6 mois : à gérer manuellement ou via webhook (incrément referral_count, recalcul date).
 */
exports.handler = async (event) => {
  const headers = {
    "Content-Type": "application/json",
  };

  if (event.httpMethod === "OPTIONS") {
    return { statusCode: 204, headers, body: "" };
  }

  if (event.httpMethod !== "POST") {
    return { statusCode: 405, headers, body: JSON.stringify({ ok: false, error: "method_not_allowed" }) };
  }

  const secret = process.env.STRIPE_SECRET_KEY;
  if (!secret) {
    return {
      statusCode: 500,
      headers,
      body: JSON.stringify({ ok: false, error: "server_misconfigured" }),
    };
  }

  let body;
  try {
    body = JSON.parse(event.body || "{}");
  } catch {
    return { statusCode: 400, headers, body: JSON.stringify({ ok: false, error: "invalid_json" }) };
  }

  const customerId = typeof body.customerId === "string" ? body.customerId.trim() : "";
  if (!customerId || !customerId.startsWith("cus_")) {
    return { statusCode: 400, headers, body: JSON.stringify({ ok: false, error: "invalid_customer" }) };
  }

  const stripe = new Stripe(secret);

  let customer;
  try {
    customer = await stripe.customers.retrieve(customerId);
  } catch (e) {
    return {
      statusCode: 200,
      headers,
      body: JSON.stringify({ ok: false, reason: "customer_not_found", error: String(e.message || e) }),
    };
  }

  if (customer.deleted) {
    return { statusCode: 200, headers, body: JSON.stringify({ ok: false, reason: "deleted" }) };
  }

  const subs = await stripe.subscriptions.list({
    customer: customerId,
    status: "all",
    limit: 20,
  });

  let periodEndSec = 0;
  for (const s of subs.data) {
    if (s.status === "active" || s.status === "trialing") {
      const end = s.current_period_end;
      if (typeof end === "number" && end > periodEndSec) periodEndSec = end;
    }
  }

  let accessUntilSec = periodEndSec;
  const md = customer.metadata || {};
  if (md.free_access_until) {
    const t = Date.parse(md.free_access_until) / 1000;
    if (!Number.isNaN(t)) {
      accessUntilSec = Math.max(accessUntilSec, t);
    }
  }

  const nowSec = Math.floor(Date.now() / 1000);
  const ok = accessUntilSec > nowSec;
  const accessUntil =
    accessUntilSec > 0 ? new Date(accessUntilSec * 1000).toISOString() : null;

  return {
    statusCode: 200,
    headers,
    body: JSON.stringify({
      ok,
      accessUntil,
      hasActiveSubscription: periodEndSec > nowSec,
      reason: ok ? undefined : "no_active_period",
    }),
  };
};
