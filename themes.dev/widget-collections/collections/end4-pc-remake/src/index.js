const FILTER_INPUT_ID = "widget-filter";
const ENTRY_SELECTOR = "[data-widget-entry]";
const GROUP_SELECTOR = "[data-widget-group]";
const STATUS_SELECTOR = "[data-filter-status]";

const filterInput = document.getElementById(FILTER_INPUT_ID);
const entries = [...document.querySelectorAll(ENTRY_SELECTOR)];
const groups = [...document.querySelectorAll(GROUP_SELECTOR)];
const status = document.querySelector(STATUS_SELECTOR);
const total = entries.length;

const normalize = (value) => String(value ?? "").trim().toLocaleLowerCase();

const updateFilter = () => {
  const query = normalize(filterInput.value);
  let visible = 0;

  for (const entry of entries) {
    const matches = query === "" || normalize(entry.dataset.search).includes(query);
    entry.hidden = !matches;
    if (matches) visible += 1;
  }

  for (const group of groups) {
    group.hidden = !group.querySelector(ENTRY_SELECTOR + ":not([hidden])");
  }

  if (query === "") {
    status.textContent = `Showing ${total} widgets`;
  } else if (visible === 0) {
    status.textContent = `No widgets match “${filterInput.value.trim()}”`;
  } else {
    status.textContent = `Showing ${visible} of ${total} widgets`;
  }
};

filterInput.addEventListener("input", updateFilter);
updateFilter();
