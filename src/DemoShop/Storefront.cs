namespace DemoShop;

// The storefront page: a vanilla-JS single-page app ("Demo Supply Co.") that calls the shop's /api.
public static class Storefront
{
    public const string Csp = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Demo Supply Co.</title>
<style>
  :root { --ink:#1f2933; --muted:#62707d; --line:#e3e7eb; --brand:#1f6f50; --bg:#f7f8f6; --bad:#b42318; }
  * { box-sizing: border-box; }
  body { margin:0; font:15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; color:var(--ink); background:var(--bg); }
  header { background:#fff; border-bottom:1px solid var(--line); }
  .bar { max-width:1040px; margin:0 auto; padding:12px 16px; display:flex; gap:16px; align-items:center; flex-wrap:wrap; }
  .brand { font-weight:700; color:var(--brand); text-decoration:none; font-size:18px; }
  nav { display:flex; gap:14px; margin-left:auto; align-items:center; }
  nav a, nav button { color:var(--ink); text-decoration:none; background:none; border:0; font:inherit; cursor:pointer; padding:0; }
  form.search { display:flex; gap:6px; }
  main { max-width:1040px; margin:0 auto; padding:20px 16px 48px; }
  input, select { font:inherit; padding:7px 9px; border:1px solid #c9d0d6; border-radius:6px; background:#fff; }
  button.primary, button.secondary { font:inherit; padding:7px 14px; border-radius:6px; cursor:pointer; border:1px solid var(--brand); }
  button.primary { background:var(--brand); color:#fff; } button.secondary { background:#fff; color:var(--brand); }
  button:disabled { opacity:.5; cursor:not-allowed; }
  .grid { display:grid; grid-template-columns:repeat(auto-fill, minmax(220px, 1fr)); gap:14px; }
  .card { background:#fff; border:1px solid var(--line); border-radius:10px; padding:14px; }
  .emoji { font-size:40px; }
  .price { font-weight:700; } .muted { color:var(--muted); font-size:13px; }
  .alert { padding:10px 12px; border-radius:6px; margin:10px 0; background:#fdecea; color:var(--bad); }
  .ok { background:#e8f5ee; color:var(--brand); }
  table { width:100%; border-collapse:collapse; background:#fff; } td, th { padding:9px; border-bottom:1px solid var(--line); text-align:left; }
  .sum { max-width:360px; margin-left:auto; } .sum div { display:flex; justify-content:space-between; padding:3px 0; }
  .field { display:flex; flex-direction:column; gap:4px; margin-bottom:12px; max-width:420px; }
  .err { color:var(--bad); font-size:13px; }
  .row { display:flex; gap:8px; align-items:center; flex-wrap:wrap; }
</style>
</head>
<body>
<header><div class="bar">
  <a class="brand" href="#/">Demo Supply Co.</a>
  <form class="search" id="search"><input name="q" placeholder="Search products" aria-label="Search products"><button class="secondary" type="submit">Search</button></form>
  <nav><a href="#/">Shop</a><a href="#/orders">Orders</a><a href="#/staff" id="staffLink" hidden>Order desk</a><a href="#/cart" id="cartLink">Cart</a><span id="who"></span></nav>
</div></header>
<main id="app"></main>
<script>
const API = location.pathname.replace(/\/$/, "") + "/api";
const $ = (s) => document.querySelector(s);
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const money = (n) => "$" + Number(n).toFixed(2);
const tok = () => { try { return localStorage.getItem("shop.session") || ""; } catch { return ""; } };
const setTok = (t) => { try { t ? localStorage.setItem("shop.session", t) : localStorage.removeItem("shop.session"); } catch {} };
let me = null, flash = "";

async function call(path, opts = {}) {
  const r = await fetch(API + path, { method: opts.method || "GET", headers: { "content-type": "application/json", "X-Sim-Session": tok() }, body: opts.json ? JSON.stringify(opts.json) : undefined });
  const body = r.status === 204 ? null : await r.json().catch(() => null);
  if (!r.ok) { const e = new Error(body?.title || body?.detail || ("Request failed (" + r.status + ")")); e.status = r.status; e.body = body; throw e; }
  return body;
}

async function refreshHeader() {
  try { me = tok() ? await call("/me") : null; } catch { me = null; setTok(""); }
  $("#who").innerHTML = me ? `<span class="muted">Hi, ${esc(me.name)}</span> <button id="logout">Sign out</button>` : `<a href="#/login">Sign in</a>`;
  $("#staffLink").hidden = me?.role !== "staff";
  if (me) { $("#logout").onclick = async () => { await call("/logout", { method: "POST" }).catch(() => {}); setTok(""); location.hash = "#/"; render(); }; }
  try { const c = me ? await call("/cart") : null; $("#cartLink").textContent = "Cart (" + (c ? c.count : 0) + ")"; } catch { $("#cartLink").textContent = "Cart"; }
}

function needLogin(e) { if (e.status === 403) { flash = "Please sign in to continue."; location.hash = "#/login"; return true; } return false; }
const alertBox = (msg, ok) => msg ? `<div class="alert ${ok ? "ok" : ""}" role="${ok ? "status" : "alert"}">${esc(msg)}</div>` : "";
function takeFlash() { const f = flash; flash = ""; return f; }

async function addToCart(id, qty, out) {
  try { await call("/cart/items", { method: "POST", json: { productId: id, qty } }); out.innerHTML = alertBox("Added to cart.", true); refreshHeader(); }
  catch (e) { if (!needLogin(e)) out.innerHTML = alertBox(e.message); }
}

const stockLabel = (s) => s <= 0 ? "Out of stock" : s <= 5 ? `Only ${s} left` : "In stock";

async function shop(params) {
  const q = params.get("q") || "", cat = params.get("category") || "";
  const data = await call("/products?" + new URLSearchParams({ q, category: cat }));
  $("#app").innerHTML = `${alertBox(takeFlash(), true)}
    <div class="row" style="justify-content:space-between;margin-bottom:12px">
      <h1 style="margin:0">${q ? "Results for “" + esc(q) + "”" : "All products"}</h1>
      <label class="row">Category <select id="cat" aria-label="Category"><option value="">All</option>${data.categories.map((c) => `<option ${c === cat ? "selected" : ""}>${esc(c)}</option>`).join("")}</select></label>
    </div>
    <div id="msg"></div>
    ${data.items.length === 0 ? `<p>No products found.</p>` : `<div class="grid">${data.items.map((p) => `
      <div class="card">
        <div class="emoji" aria-hidden="true">${p.emoji}</div>
        <h3 style="margin:6px 0"><a href="#/product/${p.id}">${esc(p.name)}</a></h3>
        <div class="price">${money(p.price)}</div>
        <div class="muted">${esc(p.category)} · ★ ${p.rating} · ${stockLabel(p.stock)}</div>
        <button class="primary" style="margin-top:10px" data-add="${p.id}" ${p.stock <= 0 ? "disabled" : ""}>Add to cart</button>
      </div>`).join("")}</div>`}`;
  $("#cat").onchange = (e) => { location.hash = "#/?" + new URLSearchParams({ q, category: e.target.value }); };
  document.querySelectorAll("[data-add]").forEach((b) => b.onclick = () => addToCart(b.dataset.add, 1, $("#msg")));
}

async function product(id) {
  const data = await call("/products");
  const p = data.items.find((x) => x.id === id);
  if (!p) { $("#app").innerHTML = "<h1>Product not found</h1>"; return; }
  $("#app").innerHTML = `<p><a href="#/">← Back to shop</a></p>
    <div class="card" style="max-width:560px">
      <div class="emoji" aria-hidden="true">${p.emoji}</div>
      <h1 style="margin:6px 0">${esc(p.name)}</h1>
      <p>${esc(p.description)}</p>
      <div class="price">${money(p.price)}</div>
      <div class="muted">${esc(p.category)} · ★ ${p.rating} · ${stockLabel(p.stock)}</div>
      <div class="row" style="margin-top:12px"><label for="qty">Quantity</label><input id="qty" type="number" min="1" value="1" style="width:80px">
      <button class="primary" id="add" ${p.stock <= 0 ? "disabled" : ""}>Add to cart</button></div>
      <div id="msg"></div>
    </div>`;
  $("#add").onclick = () => addToCart(p.id, parseInt($("#qty").value, 10), $("#msg"));
}

function login() {
  $("#app").innerHTML = `<h1>Sign in</h1>${alertBox(takeFlash())}
    <form id="f" class="card" style="max-width:440px">
      <div class="field"><label for="email">Email</label><input id="email" type="email" autocomplete="username"></div>
      <div class="field"><label for="password">Password</label><input id="password" type="password" autocomplete="current-password"></div>
      <div id="msg"></div><button class="primary" type="submit">Sign in</button>
      <p class="muted">New here? <a href="#/register">Create an account</a></p>
      <p class="muted">Demo account: shopper01@shop.demo / Shop123!</p>
    </form>`;
  $("#f").onsubmit = async (ev) => {
    ev.preventDefault();
    try { const r = await call("/login", { method: "POST", json: { email: $("#email").value, password: $("#password").value } }); setTok(r.token); flash = "Welcome back, " + r.name + "."; location.hash = "#/"; }
    catch (e) { $("#msg").innerHTML = alertBox(e.message); }
  };
}

function register() {
  $("#app").innerHTML = `<h1>Create an account</h1>
    <form id="f" class="card" style="max-width:440px">
      <div class="field"><label for="name">Full name</label><input id="name"></div>
      <div class="field"><label for="email">Email</label><input id="email" type="email"></div>
      <div class="field"><label for="password">Password</label><input id="password" type="password"></div>
      <div id="msg"></div><button class="primary" type="submit">Create account</button>
    </form>`;
  $("#f").onsubmit = async (ev) => {
    ev.preventDefault();
    try { const r = await call("/register", { method: "POST", json: { name: $("#name").value, email: $("#email").value, password: $("#password").value } }); setTok(r.token); flash = "Account created."; location.hash = "#/"; }
    catch (e) { $("#msg").innerHTML = alertBox(e.message); }
  };
}

function summary(c) {
  return `<div class="sum card">
    <div><span>Subtotal</span><span>${money(c.subtotal)}</span></div>
    <div><span>Discount</span><span>${c.discount ? "−" + money(c.discount) : money(0)}</span></div>
    <div><span>Shipping</span><span>${c.shipping ? money(c.shipping) : "Free"}</span></div>
    <div><span>Tax</span><span>${money(c.tax)}</span></div>
    ${c.giftWrapFee ? `<div><span>Gift wrap</span><span>${money(c.giftWrapFee)}</span></div>` : ""}
    <div style="font-weight:700;border-top:1px solid var(--line);margin-top:4px;padding-top:6px"><span>Total</span><span>${money(c.total)}</span></div>
  </div>`;
}

async function cart() {
  let c;
  try { c = await call("/cart"); } catch (e) { if (!needLogin(e)) throw e; return; }
  const draw = (c, msg, ok) => {
    $("#app").innerHTML = `<h1>Your cart</h1>${alertBox(msg, ok)}
      ${c.items.length === 0 ? `<p>Your cart is empty. <a href="#/">Continue shopping</a></p>` : `
      <table><thead><tr><th>Item</th><th>Price</th><th>Quantity</th><th>Line total</th><th></th></tr></thead><tbody>
      ${c.items.map((l) => `<tr>
        <td>${l.emoji} ${esc(l.name)}</td><td>${money(l.price)}</td>
        <td><div class="row"><input type="number" aria-label="Quantity for ${esc(l.name)}" value="${l.qty}" style="width:70px" data-qty="${l.productId}"><button class="secondary" data-upd="${l.productId}">Update</button></div></td>
        <td>${money(l.lineTotal)}</td><td><button class="secondary" data-rm="${l.productId}">Remove</button></td></tr>`).join("")}
      </tbody></table>
      <div class="row" style="margin:16px 0">
        <label for="coupon">Coupon code</label><input id="coupon" placeholder="e.g. SAVE10"><button class="secondary" id="apply">Apply coupon</button>
        ${c.coupons.length ? `<span class="muted">Applied: ${c.coupons.map(esc).join(", ")}</span> <button class="secondary" id="clear">Remove coupons</button>` : ""}
      </div>
      ${summary(c)}
      <p style="text-align:right"><a href="#/checkout"><button class="primary">Proceed to checkout</button></a></p>`}`;
    const act = async (fn, okMsg) => { try { draw(await fn(), okMsg, true); refreshHeader(); } catch (e) { if (!needLogin(e)) draw(c, e.message); } };
    document.querySelectorAll("[data-upd]").forEach((b) => b.onclick = () => act(() => call("/cart/items/" + b.dataset.upd, { method: "PUT", json: { qty: parseInt(document.querySelector(`[data-qty="${b.dataset.upd}"]`).value, 10) } }), "Quantity updated."));
    document.querySelectorAll("[data-rm]").forEach((b) => b.onclick = () => act(() => call("/cart/items/" + b.dataset.rm, { method: "DELETE" }), "Item removed."));
    if ($("#apply")) $("#apply").onclick = () => act(() => call("/cart/coupon", { method: "POST", json: { code: $("#coupon").value } }), "Coupon applied.");
    if ($("#clear")) $("#clear").onclick = () => act(() => call("/cart/coupon", { method: "DELETE" }), "Coupons removed.");
  };
  draw(c);
}

async function checkout() {
  let c;
  try { c = await call("/cart"); } catch (e) { if (!needLogin(e)) throw e; return; }
  if (c.items.length === 0) { $("#app").innerHTML = `<h1>Checkout</h1><p>Your cart is empty. <a href="#/">Continue shopping</a></p>`; return; }
  const f = (id, label, ph) => `<div class="field"><label for="${id}">${label}</label><input id="${id}" placeholder="${ph || ""}"><span class="err" id="${id}-err"></span></div>`;
  const opts = await call("/checkout/options").catch(() => ({}));
  $("#app").innerHTML = `<h1>Checkout</h1><div class="row" style="align-items:flex-start;gap:24px">
    <form id="f" class="card" style="flex:1;min-width:280px">
      <h2 style="margin-top:0">Shipping</h2>${f("name", "Full name")}${f("address", "Street address")}${f("city", "City")}${f("zip", "ZIP code", "12345")}
      <h2>Payment</h2>${f("cardNumber", "Card number", "4242 4242 4242 4242")}${f("expiry", "Expiry (MM/YY)", "MM/YY")}${f("cvv", "CVV", "123")}
      ${opts.giftWrapPrice ? `<p><label><input id="giftWrap" type="checkbox"> Gift wrap (${money(opts.giftWrapPrice)})</label></p>` : ""}
      <div id="msg"></div><button class="primary" type="submit">Place order</button>
    </form><div style="flex:0 0 320px">${summary(c)}</div></div>`;
  $("#f").onsubmit = async (ev) => {
    ev.preventDefault();
    document.querySelectorAll(".err").forEach((e) => e.textContent = "");
    const v = (id) => $("#" + id).value;
    try {
      const o = await call("/checkout", { method: "POST", json: { name: v("name"), address: v("address"), city: v("city"), zip: v("zip"), cardNumber: v("cardNumber"), expiry: v("expiry"), cvv: v("cvv"), giftWrap: $("#giftWrap")?.checked ?? null } });
      location.hash = "#/order/" + o.id; refreshHeader();
    } catch (e) {
      if (needLogin(e)) return;
      for (const [k, m] of Object.entries(e.body?.fieldErrors || {})) { const el = $("#" + k + "-err"); if (el) el.textContent = m; }
      $("#msg").innerHTML = alertBox(e.message);
    }
  };
}

function orderView(o, heading) {
  return `<h1>${heading}</h1><p>Order <strong>${esc(o.id)}</strong> · ${esc(o.status)} · Ships to ${esc(o.shipTo)} · Card ending ${esc(o.cardLast4)}</p>
    <table><thead><tr><th>Item</th><th>Qty</th><th>Line total</th></tr></thead><tbody>
    ${o.lines.map((l) => `<tr><td>${esc(l.name)}</td><td>${l.qty}</td><td>${money(l.lineTotal ?? l.price * l.qty)}</td></tr>`).join("")}</tbody></table>${summary(o)}`;
}

async function order(id) {
  let o;
  try { o = await call("/orders/" + encodeURIComponent(id)); }
  catch (e) { if (!needLogin(e)) $("#app").innerHTML = "<h1>Order not found</h1>"; return; }
  $("#app").innerHTML = orderView(o, o.status === "Cancelled" ? "Order cancelled" : "Thank you! Your order is confirmed.")
    + (o.status === "Processing" ? `<p><button id="cancelOrder">Cancel order</button></p><div id="msg"></div>` : "");
  if ($("#cancelOrder")) $("#cancelOrder").onclick = async () => {
    try { await call(`/orders/${encodeURIComponent(id)}/cancel`, { method: "POST" }); flash = "Order cancelled."; await order(id); }
    catch (e) { $("#msg").innerHTML = alertBox(e.message); }
  };
}

async function staff() {
  let list;
  try { list = await call("/staff/orders"); } catch (e) { if (e.status === 403 && me) { $("#app").innerHTML = alertBox("Staff only."); return; } if (!needLogin(e)) throw e; return; }
  $("#app").innerHTML = `<h1>Order desk</h1><table><thead><tr><th>Order</th><th>Customer</th><th>Date</th><th>Status</th><th>Total</th><th></th></tr></thead><tbody>
    ${list.map((o) => `<tr><td>${esc(o.id)}</td><td>${esc(o.email)}</td><td>${new Date(o.createdAt).toLocaleDateString()}</td><td>${esc(o.status)}</td><td>${money(o.total)}</td>
      <td>${o.status === "Processing" ? `<button data-next="Shipped" data-id="${esc(o.id)}">Mark shipped</button>` : o.status === "Shipped" ? `<button data-next="Delivered" data-id="${esc(o.id)}">Mark delivered</button>` : ""}</td></tr>`).join("")}</tbody></table>`;
  document.querySelectorAll("[data-next]").forEach((b) => b.onclick = async () => { await call(`/staff/orders/${encodeURIComponent(b.dataset.id)}/status`, { method: "POST", json: { status: b.dataset.next } }); await staff(); });
}

async function orders() {
  let list;
  try { list = await call("/orders"); } catch (e) { if (!needLogin(e)) throw e; return; }
  $("#app").innerHTML = `<h1>Your orders</h1>${list.length === 0 ? "<p>No orders yet.</p>" : `<table><thead><tr><th>Order</th><th>Date</th><th>Status</th><th>Total</th></tr></thead><tbody>
    ${list.map((o) => `<tr><td><a href="#/order/${esc(o.id)}">${esc(o.id)}</a></td><td>${new Date(o.createdAt).toLocaleDateString()}</td><td>${esc(o.status)}</td><td>${money(o.total)}</td></tr>`).join("")}</tbody></table>`}`;
}

async function render() {
  const [path, query] = (location.hash.slice(1) || "/").split("?");
  const params = new URLSearchParams(query || "");
  const parts = path.split("/").filter(Boolean);
  try {
    await refreshHeader();
    if (parts[0] === "product") await product(parts[1]);
    else if (parts[0] === "login") login();
    else if (parts[0] === "register") register();
    else if (parts[0] === "cart") await cart();
    else if (parts[0] === "checkout") await checkout();
    else if (parts[0] === "order") await order(parts[1]);
    else if (parts[0] === "orders") await orders();
    else if (parts[0] === "staff") await staff();
    else await shop(params);
  } catch (e) { $("#app").innerHTML = alertBox("Something went wrong: " + e.message); }
}

$("#search").onsubmit = (ev) => { ev.preventDefault(); location.hash = "#/?" + new URLSearchParams({ q: ev.target.q.value }); };
window.addEventListener("hashchange", render);
render();
</script>
</body>
</html>
""";
}
