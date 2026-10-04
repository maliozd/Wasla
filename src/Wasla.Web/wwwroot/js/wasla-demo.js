(function () {
  var form = document.querySelector(".wasla-demo-launch");
  if (!form) return;
  var pending = false;

  function token() {
    var input = form.querySelector("input[name='__RequestVerificationToken']");
    return input ? input.value : "";
  }

  function syncLaunch() {
    form.hidden = !!document.querySelector("[data-wasla-demo]");
  }

  var host = document.getElementById("ordersLiveScreenHost");
  if (host && window.MutationObserver) {
    var observer = new MutationObserver(syncLaunch);
    observer.observe(host, { childList: true, subtree: true, attributes: true, attributeFilter: ["data-wasla-demo-card"] });
  }

  form.addEventListener("submit", function (event) {
    event.preventDefault();
    if (pending) return;
    pending = true;
    form.setAttribute("aria-busy", "true");
    document.dispatchEvent(new CustomEvent("wasla:demo-launch-state"));
    var button = form.querySelector("button");
    if (button) button.disabled = true;
    fetch(form.getAttribute("action") || "/orders/demo", {
      method: "POST",
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
        "X-Requested-With": "XMLHttpRequest",
        "RequestVerificationToken": token()
      },
      body: "ajax=1&__RequestVerificationToken=" + encodeURIComponent(token()),
      credentials: "same-origin"
    }).then(function (response) {
      if (!response.ok || response.redirected) throw new Error("demo");
      return response.json();
    }).then(function (session) {
      if (!session.id) throw new Error("demo");
      document.dispatchEvent(new CustomEvent("wasla:demo-order-started", { detail: { orderId: session.id } }));
      if (window.WaslaOrders && WaslaOrders.liveStore && typeof WaslaOrders.liveStore.requestRefresh === "function") {
        WaslaOrders.liveStore.requestRefresh();
      }
    }).catch(function () {
      if (window.WaslaToast) window.WaslaToast.error(form.getAttribute("data-error-message"));
    }).finally(function () {
      pending = false;
      form.setAttribute("aria-busy", "false");
      if (button) button.disabled = false;
      document.dispatchEvent(new CustomEvent("wasla:demo-launch-state"));
    });
  });
  syncLaunch();
})();
