// The docs' sidebar and the previous/next links, from one list, so adding a page is one line here
// rather than an edit to every page. Each page has <nav class="docs-nav"> and <div class="pager">.
const pages = [
  ["Basics", [
    ["index.html", "Getting started"],
    ["keyboard.html", "Keyboard reference"],
    ["compare.html", "Choosing a file manager"],
  ]],
  ["Using it", [
    ["navigating.html", "Getting around"],
    ["files.html", "Working with files"],
    ["remote.html", "Servers and archives"],
    ["gathering.html", "Marks, shelf, collections"],
    ["git.html", "Git"],
  ]],
  ["Making it yours", [
    ["tools.html", "Tools and the command line"],
    ["scripts.html", "Listings and script locations"],
    ["configuration.html", "Configuration files"],
  ]],
];

const here = location.pathname.split("/").pop() || "index.html";
const flat = pages.flatMap(([, items]) => items);

const nav = document.querySelector(".docs-nav");
if (nav) {
  nav.innerHTML = pages.map(([heading, items]) =>
    `<h4>${heading}</h4><ul>` +
    items.map(([href, title]) =>
      `<li><a href="${href}"${href === here ? ' aria-current="page"' : ""}>${title}</a></li>`).join("") +
    "</ul>").join("");
}

const pager = document.querySelector(".pager");
const i = flat.findIndex(([href]) => href === here);
if (pager && i >= 0) {
  const prev = flat[i - 1], next = flat[i + 1];
  pager.innerHTML =
    (prev ? `<a href="${prev[0]}"><small>Previous</small>${prev[1]}</a>` : "<span></span>") +
    (next ? `<a href="${next[0]}" style="text-align:right"><small>Next</small>${next[1]}</a>` : "");
}
