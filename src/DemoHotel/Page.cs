namespace DemoHotel;

// The booking site: search rooms, book one, see and cancel your bookings (scoped to this browser), and a front desk for
// staff to see every booking and check guests in and out. Vanilla JS, no dependencies.
public static class Page
{
    public const string Html = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Demo Hotel</title>
<style>
body{font:15px/1.5 -apple-system,"Segoe UI",Roboto,sans-serif;margin:0;color:#1f2937;background:#fff}
header{display:flex;align-items:center;gap:20px;padding:14px 32px;border-bottom:1px solid #e5e7eb}
header strong{font-size:18px;color:#0f3d5e}nav a{color:#374151;text-decoration:none;margin-right:16px}
#who{margin-left:auto;font-size:13px;color:#6b7280}
main{max-width:920px;margin:24px auto;padding:0 24px}label{display:block;font-size:13px;color:#4b5563;margin-bottom:4px}
input,select{font:inherit;padding:7px 9px;border:1px solid #d1d5db;border-radius:6px}button{font:inherit;padding:8px 14px;border:0;border-radius:6px;background:#0f3d5e;color:#fff;cursor:pointer}
button.secondary{background:#fff;color:#0f3d5e;border:1px solid #0f3d5e}
.row{display:flex;gap:12px;align-items:end;flex-wrap:wrap}.card{border:1px solid #e5e7eb;border-radius:8px;padding:14px 16px;margin:12px 0;display:flex;justify-content:space-between;gap:16px}
.muted{color:#6b7280;font-size:13px}.err{color:#b91c1c;margin:10px 0}.ok{color:#047857;margin:10px 0}
table{border-collapse:collapse;width:100%}td,th{text-align:left;padding:8px;border-bottom:1px solid #e5e7eb;font-size:14px}
</style></head>
<body>
<header><strong>Demo Hotel</strong><nav><a href="#/">Book a room</a><a href="#/bookings">My bookings</a><a href="#/desk">Front desk</a></nav><span id="who"></span></header>
<main id="app"></main>
<script>
const $ = (s) => document.querySelector(s);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const day = (n) => new Date(Date.now() + n * 864e5).toISOString().slice(0, 10);
const store = { get: (k) => { try { return sessionStorage.getItem(k); } catch { return null; } }, set: (k, v) => { try { v ? sessionStorage.setItem(k, v) : sessionStorage.removeItem(k); } catch {} } };
// A guest is identified by this browser tab; the front desk by its sign-in.
let guest = store.get("guest"); if (!guest) { guest = "g-" + crypto.randomUUID(); store.set("guest", guest); }
let flash = "", search = { checkIn: day(7), checkOut: day(10), guests: 2 };
async function call(path, opts = {}) {
  const headers = { "content-type": "application/json", "X-Guest": guest };
  const staff = store.get("staff"); if (staff) headers["X-Staff-Session"] = staff;
  const r = await fetch("/api" + path, { method: opts.method || "GET", headers, body: opts.json ? JSON.stringify(opts.json) : undefined });
  const body = r.status === 204 ? null : await r.json().catch(() => null);
  if (!r.ok) throw Object.assign(new Error(body?.detail || body?.title || "Request failed (" + r.status + ")"), { status: r.status });
  return body;
}
const money = (n) => "$" + Number(n).toFixed(2);
function who() { $("#who").innerHTML = store.get("staff") ? `Front desk · <a href="#/desk/logout">Sign out</a>` : ""; }
async function home() {
  const rooms = (await call("/rooms")).items.filter((r) => r.maxGuests >= (search.guests || 1));
  $("#app").innerHTML = `<h1>Book a room</h1>${flash ? `<p class="ok">${esc(flash)}</p>` : ""}
    <form id="search" class="row">
      <div><label for="checkIn">Check-in</label><input id="checkIn" type="text" value="${search.checkIn}" placeholder="yyyy-mm-dd"></div>
      <div><label for="checkOut">Check-out</label><input id="checkOut" type="text" value="${search.checkOut}" placeholder="yyyy-mm-dd"></div>
      <div><label for="guests">Guests</label><input id="guests" type="number" value="${search.guests}" style="width:80px"></div>
      <button>Search</button></form>
    ${rooms.map((r) => `<div class="card"><div><strong>${esc(r.name)}</strong><div class="muted">${esc(r.description)} Sleeps ${r.maxGuests}.${r.breakfastRate ? ` Breakfast ${money(r.breakfastRate)} per guest per night.` : ""}</div></div>
      <div style="text-align:right">${money(r.rate)} / night<br><button data-room="${r.id}">Book</button></div></div>`).join("") || "<p>No rooms for that many guests.</p>"}`;
  flash = "";
  $("#search").onsubmit = (e) => { e.preventDefault(); search = { checkIn: $("#checkIn").value, checkOut: $("#checkOut").value, guests: Number($("#guests").value) }; home(); };
  document.querySelectorAll("[data-room]").forEach((b) => b.onclick = () => { location.hash = "#/book/" + b.dataset.room; });
}
async function book(id) {
  const room = (await call("/rooms")).items.find((r) => r.id === id);
  $("#app").innerHTML = `<h1>Book ${esc(room.name)}</h1><p class="muted">${money(room.rate)} per night, sleeps ${room.maxGuests}.</p>
    <form id="bookf"><div class="row">
      <div><label for="bin">Check-in</label><input id="bin" value="${search.checkIn}"></div>
      <div><label for="bout">Check-out</label><input id="bout" value="${search.checkOut}"></div>
      <div><label for="bguests">Guests</label><input id="bguests" type="number" value="${search.guests}" style="width:80px"></div></div>
      ${room.breakfastRate ? `<p><label><input id="breakfast" type="checkbox"> Add breakfast (${money(room.breakfastRate)} per guest per night)</label></p>` : ""}
      <div class="row" style="margin-top:12px"><div><label for="name">Full name</label><input id="name"></div><div><label for="email">Email</label><input id="email"></div></div>
      <p><button>Confirm booking</button></p><div id="msg"></div></form>`;
  $("#bookf").onsubmit = async (e) => {
    e.preventDefault();
    try {
      const b = await call("/bookings", { method: "POST", json: { roomId: id, checkIn: $("#bin").value, checkOut: $("#bout").value, guests: Number($("#bguests").value), name: $("#name").value, email: $("#email").value, breakfast: $("#breakfast")?.checked ?? null } });
      flash = `Booked ${b.roomName}: ${b.nights} nights, total ${money(b.total)}. Reference ${b.id}.`; location.hash = "#/bookings";
    } catch (err) { $("#msg").innerHTML = `<p class="err">${esc(err.message)}</p>`; }
  };
}
async function bookings() {
  const list = await call("/bookings");
  $("#app").innerHTML = `<h1>My bookings</h1>${flash ? `<p class="ok">${esc(flash)}</p>` : ""}` + (list.items.map((b) => `<div class="card"><div><strong>${esc(b.roomName)}</strong> <span class="muted">${b.id}</span>
    <div class="muted">${b.checkIn} to ${b.checkOut} · ${b.nights} nights · ${b.guests} guests${b.breakfast ? " · breakfast" : ""} · ${b.status}</div></div>
    <div style="text-align:right">${money(b.total)}<br>${b.status === "Confirmed" ? `<button data-cancel="${b.id}">Cancel</button>` : ""}</div></div>`).join("") || "<p>No bookings yet.</p>");
  flash = "";
  document.querySelectorAll("[data-cancel]").forEach((b) => b.onclick = async () => { await call(`/bookings/${b.dataset.cancel}/cancel`, { method: "POST" }); bookings(); });
}
async function deskLogin() {
  $("#app").innerHTML = `<h1>Front desk sign-in</h1>${flash ? `<p class="err">${esc(flash)}</p>` : ""}
    <form id="loginf"><div class="row"><div><label for="demail">Email</label><input id="demail"></div><div><label for="dpass">Password</label><input id="dpass" type="password"></div><button>Sign in</button></div><div id="msg"></div></form>`;
  flash = "";
  $("#loginf").onsubmit = async (e) => {
    e.preventDefault();
    try { const s = await call("/staff/login", { method: "POST", json: { email: $("#demail").value, password: $("#dpass").value } }); store.set("staff", s.token); history.replaceState(null, "", "#/desk"); render(); } // the form is shown at #/desk, so no hashchange
    catch (err) { $("#msg").innerHTML = `<p class="err">${esc(err.message)}</p>`; }
  };
}
async function desk() {
  let list;
  try { list = await call("/staff/bookings"); }
  catch (err) { if (err.status === 401) { flash = "Please sign in as front-desk staff."; return deskLogin(); } throw err; }
  $("#app").innerHTML = `<h1>Front desk</h1><table><thead><tr><th>Booking</th><th>Guest</th><th>Room</th><th>Stay</th><th>Status</th><th>Total</th><th></th></tr></thead><tbody>
    ${list.items.map((b) => `<tr><td>${b.id}</td><td>${esc(b.name)}<div class="muted">${esc(b.email)}</div></td><td>${esc(b.roomName)}</td><td>${b.checkIn} to ${b.checkOut}</td><td>${b.status}</td><td>${money(b.total)}</td>
      <td>${b.status === "Confirmed" ? `<button data-in="${b.id}">Check in</button>` : b.status === "CheckedIn" ? `<button class="secondary" data-out="${b.id}">Check out</button>` : ""}</td></tr>`).join("") || `<tr><td colspan="7">No bookings.</td></tr>`}
    </tbody></table>`;
  document.querySelectorAll("[data-in]").forEach((b) => b.onclick = async () => { await call(`/staff/bookings/${b.dataset.in}/checkin`, { method: "POST" }); desk(); });
  document.querySelectorAll("[data-out]").forEach((b) => b.onclick = async () => { await call(`/staff/bookings/${b.dataset.out}/checkout`, { method: "POST" }); desk(); });
}
async function deskLogout() { await call("/staff/logout", { method: "POST" }).catch(() => {}); store.set("staff", null); location.hash = "#/"; }
function render() {
  const h = location.hash.slice(1) || "/";
  who();
  const page = h.startsWith("/book/") ? book(h.slice(6)) : h === "/bookings" ? bookings() : h === "/desk" ? desk() : h === "/desk/logout" ? deskLogout() : home();
  page.catch((e) => { $("#app").innerHTML = `<p class="err">${esc(e.message)}</p>`; });
}
window.onhashchange = render; render();
</script>
</body></html>
""";
}
