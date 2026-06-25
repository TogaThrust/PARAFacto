const Stripe = require("stripe");

function parseCsvEnv(value) {
  if (!value) return [];
  return String(value)
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);
}

function containsAny(haystack, needles) {
  if (!Array.isArray(needles) || needles.length === 0) return false;
  return needles.some((n) => haystack.has(n));
}

async function isParafactoSession(stripe, session) {
  const md = session.metadata || {};
  const metadataFlag =
    String(md.product_line || "").toLowerCase() === "parafacto_native" ||
    String(md.app || "").toLowerCase() === "parafacto" ||
    String(md.brand || "").toLowerCase() === "parafacto";
  if (metadataFlag) return true;

  const allowPaymentLinkIds = new Set(parseCsvEnv(process.env.PARAFACTO_PAYMENT_LINK_IDS));
  const paymentLinkId = typeof session.payment_link === "string" ? session.payment_link : "";
  if (paymentLinkId && allowPaymentLinkIds.has(paymentLinkId)) return true;

  const allowPriceIds = new Set(parseCsvEnv(process.env.PARAFACTO_PRICE_IDS));
  if (allowPriceIds.size === 0) return false;

  const li = await stripe.checkout.sessions.listLineItems(session.id, { limit: 100 });
  const seenPriceIds = new Set(
    (li.data || [])
      .map((x) => (x.price && x.price.id ? String(x.price.id) : ""))
      .filter(Boolean)
  );
  return containsAny(seenPriceIds, Array.from(allowPriceIds));
}

/**
 * GET ?session_id=cs_...
 * Retourne { customerId } pour affichage après Stripe Checkout (page success).
 */
exports.handler = async (event) => {
  const headers = { "Content-Type": "application/json" };

  if (event.httpMethod === "OPTIONS") {
    return { statusCode: 204, headers, body: "" };
  }

  if (event.httpMethod !== "GET") {
    return { statusCode: 405, headers, body: JSON.stringify({ error: "method_not_allowed" }) };
  }

  const secret = process.env.STRIPE_SECRET_KEY;
  if (!secret) {
    return { statusCode: 500, headers, body: JSON.stringify({ error: "server_misconfigured" }) };
  }

  const sessionId =
    event.queryStringParameters && event.queryStringParameters.session_id
      ? event.queryStringParameters.session_id.trim()
      : "";

  if (!sessionId || !sessionId.startsWith("cs_")) {
    return { statusCode: 400, headers, body: JSON.stringify({ error: "invalid_session" }) };
  }

  const stripe = new Stripe(secret);

  try {
    const session = await stripe.checkout.sessions.retrieve(sessionId, {
      expand: ["customer"],
    });
    if (!(await isParafactoSession(stripe, session))) {
      return {
        statusCode: 200,
        headers,
        body: JSON.stringify({ customerId: null, error: "non_parafacto_checkout" }),
      };
    }

    let customerId = null;
    if (typeof session.customer === "string") {
      customerId = session.customer;
    } else if (session.customer && !session.customer.deleted) {
      customerId = session.customer.id;
    }
    return {
      statusCode: 200,
      headers,
      body: JSON.stringify({ customerId }),
    };
  } catch (e) {
    return {
      statusCode: 400,
      headers,
      body: JSON.stringify({ error: String(e.message || e) }),
    };
  }
};
