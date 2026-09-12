// A bot-published brand overrides the editable static defaults on both pages.
window.ArchiveBranding = (() => {
  const defaults = { name: "Yoko", logoLetter: "Y", archiveTitle: "Character Archive",
    atlasTitle: "Relationship Atlas", tagline: "An index of approved lives, legends, and questionable alibis." };
  const config = fetch("./site-config.json", { cache: "no-store" })
    .then(response => response.ok ? response.json() : {}).catch(() => ({}));
  function clean(value) {
    return Object.fromEntries(Object.keys(defaults).filter(key => typeof value?.[key] === "string" && value[key].trim())
      .map(key => [key, value[key].trim().slice(0, key === "tagline" ? 240 : 80)]));
  }
  async function apply(published) {
    const brand = { ...defaults, ...clean(await config), ...clean(published) };
    brand.logoLetter = [...brand.logoLetter][0] || [...brand.name][0];
    const render = template => template.replace(/\{(name|logoLetter|archiveTitle|atlasTitle|tagline)\}/g, (_, key) => brand[key]);
    for (const node of document.querySelectorAll("[data-site-text]")) node.textContent = render(node.dataset.siteText);
    for (const node of document.querySelectorAll("[data-site-content]")) node.content = render(node.dataset.siteContent);
    document.title = render(document.body.dataset.siteTitle || "{name} · {archiveTitle}");
    return brand;
  }
  return { apply };
})();
