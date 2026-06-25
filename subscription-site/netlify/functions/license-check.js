const Stripe = require("stripe");

function parseCsvEnv(value) {
  if (!value) return [];
  return String(value)
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);
}

function isParafactoMetadata(md) {
  return (
    String((md && md.product_line) || "").toLowerCase() === "parafacto_native" ||
    String((md && md.app) || "").toLowerCase() === "parafacto" ||
    String((md && md.brand) || "").toLowerCase() === "parafacto"
  );
}

function isParafactoSubscription(subscription, allowPriceIds) {
  if (!subscription) return false;
  if (isParafactoMetadata(subscription.metadata)) return true;

  const items = subscription.items && Array.isArray(subscription.items.data)
    ? subscription.items.data
    : [];

  for (const item of items) {
    const price = item && item.price;
    const priceId = price && typeof price.id === "string" ? price.id : "";
    if (allowPriceIds.has(priceId)) return true;
    if (isParafactoMetadata(price && price.metadata)) return true;

    const product = price && price.product && typeof price.product === "object"
      ? price.product
      : null;
    if (product && isParafactoMetadata(product.metadata)) return true;
  }

  return false;
}

/**
 * POST body JSON: { customerId, deviceId }
 * Env: STRIPE_SECRET_KEY
 *
 * Un abonnement actif n'est accepté que depuis l'appareil enregistré une première fois :
 * metadata Stripe du client : parafacto_native_device = deviceId (fixé au premier accès OK).
 * Support : vider cette clé dans le dashboard Stripe (client > Métadonnées) pour transférer de PC.
 *
 * Parrainage / mois gratuits : metadata.free_access_until (ISO 8601 UTC).
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

  const deviceId = typeof body.deviceId === "string" ? body.deviceId.trim() : "";
  if (!deviceId || deviceId.length < 8) {
    return {
      statusCode: 400,
      headers,
      body: JSON.stringify({ ok: false, error: "device_required", reason: "device_required" }),
    };
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
    expand: ["data.items.data.price"],
  });

  const allowPriceIds = new Set(parseCsvEnv(process.env.PARAFACTO_PRICE_IDS));
  const md = customer.metadata || {};
  const customerIsParafacto = isParafactoMetadata(md);
  let periodEndSec = 0;
  for (const s of subs.data) {
    if (s.status !== "active" && s.status !== "trialing") continue;
    const end = s.current_period_end;
    if (typeof end !== "number") continue;
    if (isParafactoSubscription(s, allowPriceIds) || customerIsParafacto) {
      if (end > periodEndSec) periodEndSec = end;
    }
  }
  let accessUntilSec = periodEndSec;
  if (md.free_access_until) {
    const t = Date.parse(md.free_access_until) / 1000;
    if (!Number.isNaN(t)) {
      accessUntilSec = Math.max(accessUntilSec, t);
    }
  }

  const nowSec = Math.floor(Date.now() / 1000);
  const subscriptionOk = accessUntilSec > nowSec;
  const accessUntil =
    accessUntilSec > 0 ? new Date(accessUntilSec * 1000).toISOString() : null;

  if (!subscriptionOk) {
    return {
      statusCode: 200,
      headers,
      body: JSON.stringify({
        ok: false,
        accessUntil,
        hasActiveSubscription: periodEndSec > nowSec,
        reason: "no_active_period",
      }),
    };
  }

  const bound = (md.parafacto_native_device || "").trim();
  if (!bound) {
    await stripe.customers.update(customerId, {
      metadata: { parafacto_native_device: deviceId },
    });
  } else if (bound !== deviceId) {
    return {
      statusCode: 200,
      headers,
      body: JSON.stringify({
        ok: false,
        accessUntil,
        hasActiveSubscription: true,
        reason: "device_already_bound",
      }),
    };
  }

  return {
    statusCode: 200,
    headers,
    body: JSON.stringify({
      ok: true,
      accessUntil,
      hasActiveSubscription: periodEndSec > nowSec,
    }),
  };
};
