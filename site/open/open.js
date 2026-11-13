// Turns https://…/open/#connect/host[:port][?id=…] into sessionwright://connect/… and hands it to the app. The part after
// "#" never reaches the web server; only a plain host name or IP address (and a connection id) is passed on, the same
// rule the app applies to the links it receives.
(function () {
  "use strict";
  var polish = (navigator.language || "").toLowerCase().indexOf("pl") === 0;
  var text = polish ? {
    opening: "Otwieram Sessionwright…",
    invalid: "Ten link nie wskazuje serwera",
    invalidHelp: "Mógł zostać ucięty albo zmieniony. Poproś o niego ponownie.",
    open: "Otwórz w Sessionwright",
    help: "Nic się nie stało? Sessionwright może nie być zainstalowany na tym komputerze.",
    download: "Pobierz Sessionwright"
  } : {
    opening: "Opening Sessionwright…",
    invalid: "This link does not point at a server",
    invalidHelp: "It may have been cut off or changed. Ask for it again.",
    open: "Open in Sessionwright",
    help: "Nothing happened? Sessionwright may not be installed on this PC.",
    download: "Download Sessionwright"
  };
  if (polish) document.documentElement.lang = "pl";

  var link = /^connect\/(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.\-]+)(:\d{1,5})?\/?(\?id=[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})?$/
    .exec(decodeURIComponent(location.hash.slice(1)));
  var title = document.getElementById("title");
  var help = document.getElementById("help");
  document.getElementById("download").textContent = text.download;

  if (!link || (link[2] && Number(link[2].slice(1)) > 65535)) {
    title.textContent = text.invalid;
    document.getElementById("help-text").textContent = text.invalidHelp + " ";
    help.hidden = false;
    return;
  }

  var target = "sessionwright://connect/" + link[1] + (link[2] || "") + (link[3] || "");
  title.textContent = text.opening;
  document.getElementById("server").textContent = link[1] + (link[2] || "");
  var open = document.getElementById("open");
  open.href = target;
  open.textContent = text.open;
  document.getElementById("help-text").textContent = text.help + " ";
  location.href = target;
  // A browser that asks first, or one with nothing to open the link with, leaves the page as it is: offer the button
  // and the download a moment later.
  setTimeout(function () { open.hidden = false; help.hidden = false; }, 1500);
})();
