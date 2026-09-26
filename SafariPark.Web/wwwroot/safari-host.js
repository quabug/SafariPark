// DOM helpers the managed side calls through [JSImport]. Kept out of main.js because main.js
// is the dotnet bootstrap and importing it a second time would re-run it.

export function setStatus(text) {
    document.getElementById('hud-status').textContent = text;
}

export function setStats(text) {
    document.getElementById('hud-stats').textContent = text;
}

// labelsAndSpecies is a flat array ["羚羊 1","Antelope", ...] — rebuilt only when the
// selection changes, so this stays cheap.
export function setRoster(labelsAndSpecies, selected) {
    const roster = document.getElementById('roster');
    roster.innerHTML = '';
    const count = labelsAndSpecies.length / 2;
    for (let i = 0; i < count; i++) {
        const li = document.createElement('li');
        li.textContent = labelsAndSpecies[i * 2];
        if (i === selected) li.className = 'sel';
        li.addEventListener('click', () => {
            const program = window.__safariProgram;
            if (program) program.OnSelectAnimal(i);
        });
        roster.appendChild(li);
    }
}
