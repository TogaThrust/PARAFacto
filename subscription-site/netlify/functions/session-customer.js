const Stripe = require("stripe");

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
