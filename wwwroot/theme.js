const storageKey = "padel-match-sync-theme";

export function loadTheme() {
    const isDark = localStorage.getItem(storageKey) === "dark";
    document.documentElement.classList.toggle("theme-dark", isDark);
    return isDark;
}

export function saveTheme(isDark) {
    localStorage.setItem(storageKey, isDark ? "dark" : "light");
    document.documentElement.classList.toggle("theme-dark", isDark);
}
