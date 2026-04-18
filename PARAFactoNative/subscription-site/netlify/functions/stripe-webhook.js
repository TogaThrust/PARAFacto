/**
 * Webhook Stripe — e-mails post-checkout PARAFacto.
 *
 * Netlify — au choix (le webhook accepte l’un OU l’autre OU les métadonnées produit) :
 *   PARAFACTO_PAYMENT_LINK_IDS = plink_... (ID du Payment Link ; suffit souvent si checkout via buy.stripe.com)
 *   PARAFACTO_PRICE_IDS = price_...,price_... (ex. promo 19,99 price_1TNE3n1Uq3SAnIi7m8Y965Z6 + 29,99 price_1TNE4U1Uq3SAnIi7edfHWutA)
 * Ancien prix archivé : price_1TJfyS1Uq3SAnIi7lS49bvvo — à ne plus lister si vous utilisez PARAFACTO_PRICE_IDS.
 */
const Stripe = require("stripe");

function parseCsvEnv(value) {
  if (!value) return [];
  return String(value)
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);
}

function normalizeBaseUrl(url) {
  return String(url || "").trim().replace(/\/+$/, "");
}

function containsAny(haystack, needles) {
  if (!Array.isArray(needles) || needles.length === 0) return false;
  return needles.some((n) => haystack.has(n));
}

exports.handler = async (event) => {
  const headers = { "Content-Type": "application/json" };

  if (event.httpMethod === "OPTIONS") {
    return { statusCode: 204, headers, body: "" };
  }

  if (event.httpMethod !== "POST") {
    return {
      statusCode: 405,
      headers,
      body: JSON.stringify({ ok: false, error: "method_not_allowed" }),
    };
  }

  const stripeSecret = process.env.STRIPE_SECRET_KEY;
  const webhookSecret = process.env.STRIPE_WEBHOOK_SECRET;
  const resendApiKey = process.env.RESEND_API_KEY;

  if (!stripeSecret || !webhookSecret || !resendApiKey) {
    return {
      statusCode: 500,
      headers,
      body: JSON.stringify({ ok: false, error: "server_misconfigured" }),
    };
  }

  const stripe = new Stripe(stripeSecret);
  const sig = event.headers["stripe-signature"] || event.headers["Stripe-Signature"];
  if (!sig) {
    return {
      statusCode: 400,
      headers,
      body: JSON.stringify({ ok: false, error: "missing_signature" }),
    };
  }

  let stripeEvent;
  try {
    stripeEvent = stripe.webhooks.constructEvent(event.body || "", sig, webhookSecret);
  } catch (e) {
    return {
      statusCode: 400,
      headers,
      body: JSON.stringify({ ok: false, error: "invalid_signature", details: String(e.message || e) }),
    };
  }

  if (stripeEvent.type !== "checkout.session.completed") {
    return { statusCode: 200, headers, body: JSON.stringify({ ok: true, ignored: stripeEvent.type }) };
  }

  const session = stripeEvent.data.object;
  const sessionId = session.id;
  const paymentLinkId = typeof session.payment_link === "string" ? session.payment_link : "";
  const md = session.metadata || {};

  // --- Filtre PARAFACTO uniquement (pour ne pas impacter CLQ) ---
  // 1) metadata explicite sur le Payment Link / Checkout Session
  const metadataFlag =
    String(md.product_line || "").toLowerCase() === "parafacto_native" ||
    String(md.app || "").toLowerCase() === "parafacto" ||
    String(md.brand || "").toLowerCase() === "parafacto";

  // 2) payment link id explicitement autorisé (env)
  const allowPaymentLinkIds = new Set(parseCsvEnv(process.env.PARAFACTO_PAYMENT_LINK_IDS));
  const paymentLinkMatch = paymentLinkId && allowPaymentLinkIds.has(paymentLinkId);

  // 3) price id explicitement autorisé (env)
  const allowPriceIds = new Set(parseCsvEnv(process.env.PARAFACTO_PRICE_IDS));
  let priceMatch = false;
  if (allowPriceIds.size > 0) {
    try {
      const li = await stripe.checkout.sessions.listLineItems(sessionId, { limit: 100 });
      const seenPriceIds = new Set(
        (li.data || [])
          .map((x) => (x.price && x.price.id ? String(x.price.id) : ""))
          .filter(Boolean)
      );
      priceMatch = containsAny(seenPriceIds, Array.from(allowPriceIds));
    } catch {
      // On ne bloque pas : si la lecture des line items échoue, on tombe sur les autres filtres.
    }
  }

  const isParafacto = metadataFlag || paymentLinkMatch || priceMatch;
  if (!isParafacto) {
    return { statusCode: 200, headers, body: JSON.stringify({ ok: true, ignored: "non_parafacto_checkout" }) };
  }

  const toEmail =
    (session.customer_details && session.customer_details.email) ||
    session.customer_email ||
    null;
  if (!toEmail) {
    return { statusCode: 200, headers, body: JSON.stringify({ ok: true, ignored: "missing_customer_email" }) };
  }

  const appUrl = normalizeBaseUrl(process.env.PARAFACTO_APP_URL || "https://parafacto.netlify.app");
  const successUrl = `${appUrl}/success.html?session_id=${encodeURIComponent(sessionId)}`;
  const fromEmail = (process.env.PARAFACTO_FROM_EMAIL || "parafacto@parafacto.be").trim();

  const subject = "PARAFacto Native - Activation de votre abonnement";
  const text =
    `Bonjour,\n\n` +
    `Merci pour votre souscription a PARAFacto Native.\n\n` +
    `Etape suivante :\n` +
    `1) Ouvrez la page de confirmation : ${successUrl}\n` +
    `2) Cliquez sur "Copier l'identifiant"\n` +
    `3) Collez l'identifiant dans PARAFacto Native au premier lancement\n\n` +
    `Telechargement de l'application : ${appUrl}\n\n` +
    `L'equipe PARAFacto`;

  const html =
    `<p>Bonjour,</p>` +
    `<p>Merci pour votre souscription a <strong>PARAFacto Native</strong>.</p>` +
    `<p>Etape suivante :</p>` +
    `<ol>` +
    `<li>Ouvrez la page de confirmation : <a href="${successUrl}">${successUrl}</a></li>` +
    `<li>Cliquez sur <strong>Copier l'identifiant</strong></li>` +
    `<li>Collez l'identifiant dans PARAFacto Native au premier lancement</li>` +
    `</ol>` +
    `<p>Telechargement de l'application : <a href="${appUrl}">${appUrl}</a></p>` +
    `<p>L'equipe PARAFacto</p>`;

  try {
    const mailResp = await fetch("https://api.resend.com/emails", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${resendApiKey}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        from: fromEmail,
        to: [toEmail],
        subject,
        text,
        html,
      }),
    });

    if (!mailResp.ok) {
      const errBody = await mailResp.text();
      return {
        statusCode: 500,
        headers,
        body: JSON.stringify({ ok: false, error: "resend_send_failed", details: errBody }),
      };
    }
  } catch (e) {
    return {
      statusCode: 500,
      headers,
      body: JSON.stringify({ ok: false, error: "resend_request_failed", details: String(e.message || e) }),
    };
  }

  return { statusCode: 200, headers, body: JSON.stringify({ ok: true }) };
};

