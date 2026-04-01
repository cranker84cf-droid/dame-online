const protocol = location.protocol === "https:" ? "wss" : "ws";
const socket = new WebSocket(`${protocol}://${location.host}/ws`);

const state = {
  connected: false,
  playerName: "",
  currentScreen: "name",
  roomCode: "",
  mySide: null,
  isHost: false,
  snapshot: null,
  selectedPieceId: null,
  targets: [],
  ready: false,
  roomsRefreshId: null,
};

const els = {
  statusBanner: document.querySelector("#statusBanner"),
  playerName: document.querySelector("#playerName"),
  confirmNameBtn: document.querySelector("#confirmNameBtn"),
  confirmedNameValue: document.querySelector("#confirmedNameValue"),
  playerWinsValue: document.querySelector("#playerWinsValue"),
  roomCode: document.querySelector("#roomCode"),
  createRoomBtn: document.querySelector("#createRoomBtn"),
  joinRoomBtn: document.querySelector("#joinRoomBtn"),
  openRoomsList: document.querySelector("#openRoomsList"),
  roomCodeValue: document.querySelector("#roomCodeValue"),
  hostNameValue: document.querySelector("#hostNameValue"),
  playerSideValue: document.querySelector("#playerSideValue"),
  setupStatusValue: document.querySelector("#setupStatusValue"),
  readyBtn: document.querySelector("#readyBtn"),
  startMatchBtn: document.querySelector("#startMatchBtn"),
  whiteReadyLabel: document.querySelector("#whiteReadyLabel"),
  blackReadyLabel: document.querySelector("#blackReadyLabel"),
  rulesForm: document.querySelector("#rulesForm"),
  board: document.querySelector("#board"),
  turnHint: document.querySelector("#turnHint"),
  drawBtn: document.querySelector("#drawBtn"),
  resignBtn: document.querySelector("#resignBtn"),
  countdownOverlay: document.querySelector("#countdownOverlay"),
  countdownValue: document.querySelector("#countdownValue"),
  screens: {
    name: document.querySelector("#screen-name"),
    room: document.querySelector("#screen-room"),
    setup: document.querySelector("#screen-setup"),
    game: document.querySelector("#screen-game"),
  }
};

function send(type, payload = {}) {
  if (socket.readyState !== WebSocket.OPEN) {
    setBanner("Die Verbindung ist noch nicht bereit.");
    return;
  }
  socket.send(JSON.stringify({ type, payload }));
}

socket.addEventListener("open", () => {
  state.connected = true;
  setBanner("Verbunden. Bitte erst den Namen bestaetigen.");
});

socket.addEventListener("close", () => {
  state.connected = false;
  setBanner("Verbindung getrennt.");
});

socket.addEventListener("message", ({ data }) => {
  const { type, payload } = JSON.parse(data);
  if (type === "roomJoined") {
    state.roomCode = payload.roomCode;
    state.mySide = payload.side;
    state.isHost = payload.isHost;
    showScreen("setup");
  } else if (type === "snapshot") {
    state.snapshot = payload;
    state.selectedPieceId = payload.selectedPieceId;
    state.targets = payload.forcedDestinations || [];
    state.ready = readDict(payload.readyStates, normalizeSide(state.mySide)) ?? false;
    const phase = payload.phase;
    showScreen(phase === "game" || phase === "countdown" ? "game" : state.roomCode ? "setup" : state.currentScreen);
    renderAll();
  } else if (type === "error" || type === "toast") {
    setBanner(payload.message);
  }
});

els.confirmNameBtn.addEventListener("click", () => {
  const name = els.playerName.value.trim();
  if (!name) {
    setBanner("Bitte gib zuerst deinen Namen ein.");
    return;
  }
  state.playerName = name;
  els.confirmedNameValue.textContent = name;
  showScreen("room");
  setBanner(`Willkommen ${name}. Jetzt kannst du einen Raum erstellen oder betreten.`);
  loadPlayerStats();
  loadOpenRooms();
});

els.createRoomBtn.addEventListener("click", () => send("createRoom", { name: state.playerName }));
els.joinRoomBtn.addEventListener("click", () => send("joinRoom", { name: state.playerName, roomCode: els.roomCode.value.trim().toUpperCase() }));

els.readyBtn.addEventListener("click", () => send("setReady", { ready: !state.ready }));
els.startMatchBtn.addEventListener("click", () => send("startMatch", {}));
els.drawBtn.addEventListener("click", () => {
  const offered = readDict(state.snapshot?.drawOffers, normalizeSide(state.mySide));
  send("setDrawOffer", { offered: !offered });
});
els.resignBtn.addEventListener("click", () => send("resign", {}));

els.rulesForm.addEventListener("submit", (event) => {
  event.preventDefault();
  if (!state.isHost) {
    setBanner("Nur der Host darf die Regeln aendern.");
    return;
  }
  const form = new FormData(els.rulesForm);
  send("updateRules", {
    rules: {
      mandatoryCapture: form.get("mandatoryCapture") === "on",
      allowMultiCapture: form.get("allowMultiCapture") === "on",
      requireMultiCapture: form.get("requireMultiCapture") === "on",
      menCanCaptureBackward: form.get("menCanCaptureBackward") === "on",
      kingsMoveMultipleSquares: form.get("kingsMoveMultipleSquares") === "on",
      kingsMustLandDirectlyAfterCapture: form.get("kingsMustLandDirectlyAfterCapture") === "on",
      kingsMustCapture: form.get("kingsMustCapture") === "on",
      kingsCanMultiCapture: form.get("kingsCanMultiCapture") === "on",
      kingsCanChangeDirectionDuringMultiCapture: form.get("kingsCanChangeDirectionDuringMultiCapture") === "on",
      missedCapturePenalty: Number(form.get("missedCapturePenalty")),
      missedCaptureTimePenaltySeconds: Number(form.get("missedCaptureTimePenaltySeconds")),
    }
  });
});

function showScreen(name) {
  state.currentScreen = name;
  for (const [key, element] of Object.entries(els.screens)) {
    element.classList.toggle("active", key === name);
  }
  document.body.classList.toggle("game-mode", name === "game");
  if (name === "room") {
    startRoomRefresh();
  } else {
    stopRoomRefresh();
  }
}

function renderAll() {
  renderMeta();
  renderReady();
  syncRulesForm();
  renderGameHud();
  renderBoard();
  renderCountdown();
}

function renderMeta() {
  const snap = state.snapshot;
  if (!snap) return;
  els.roomCodeValue.textContent = snap.roomCode;
  els.hostNameValue.textContent = snap.hostName;
  els.playerSideValue.textContent = humanSide(state.mySide);
  els.setupStatusValue.textContent = snap.statusMessage;
  setBanner(snap.statusMessage);
}

function renderReady() {
  const snap = state.snapshot;
  if (!snap) return;
  const whiteReady = readDict(snap.readyStates, "white");
  const blackReady = readDict(snap.readyStates, "black");
  els.whiteReadyLabel.textContent = `Weiss: ${whiteReady ? "bereit" : "wartet"}`;
  els.blackReadyLabel.textContent = `Schwarz: ${blackReady ? "bereit" : "wartet"}`;
  els.readyBtn.textContent = state.ready ? "Bereitschaft aufheben" : "Ich bin bereit";
  els.startMatchBtn.disabled = !state.isHost || !whiteReady || !blackReady;
}

function renderGameHud() {
  const snap = state.snapshot;
  if (!snap) return;
  const turnSide = normalizeSide(snap.currentTurn);
  const turnName = readDict(snap.players, turnSide) || humanSide(turnSide);
  const myOffer = readDict(snap.drawOffers, normalizeSide(state.mySide));
  const opponentSide = normalizeSide(state.mySide) === "white" ? "black" : "white";
  const opponentOffer = readDict(snap.drawOffers, opponentSide);
  const time = fmtTime(readDict(snap.remainingTurnMs, turnSide));
  els.turnHint.textContent = `Am Zug: ${turnName} (${time})${opponentOffer && !myOffer ? " | Gegner bietet Remis an" : ""}`;
  els.drawBtn.textContent = opponentOffer && !myOffer ? "Remis annehmen" : (myOffer ? "Remisangebot zurueckziehen" : "Remis anbieten");
}

function renderBoard() {
  els.board.innerHTML = "";
  const snap = state.snapshot;
  const pieces = snap?.pieces ? Object.values(snap.pieces) : [];
  for (let row = 0; row < 10; row++) {
    for (let col = 0; col < 10; col++) {
      const square = document.createElement("button");
      square.type = "button";
      square.className = `square ${(row + col) % 2 === 0 ? "light" : "dark"}`;
      square.addEventListener("click", () => onSquareClick(row, col));

      const piece = pieces.find((entry) => entry.row === row && entry.col === col);
      if (piece) {
        const el = document.createElement("div");
        const side = normalizeSide(piece.side);
        el.className = `piece ${side} ${piece.isKing ? "king" : ""}`;
        el.style.background = side === "white" ? "#f4f0e8" : "#202020";
        if (piece.id === state.selectedPieceId) el.classList.add("selected");
        el.dataset.symbol = piece.isKing ? "D" : "";
        el.addEventListener("click", (event) => {
          event.stopPropagation();
          if (side === normalizeSide(state.mySide) && state.snapshot?.phase === "game") {
            send("selectPiece", { pieceId: piece.id });
          }
        });
        square.appendChild(el);
      }
      els.board.appendChild(square);
    }
  }
}

function renderCountdown() {
  const snap = state.snapshot;
  const countdownEnds = snap?.countdownEndsAtUnixMs;
  if (snap?.phase !== "countdown" || !countdownEnds) {
    els.countdownOverlay.classList.add("hidden");
    return;
  }

  const seconds = Math.max(0, Math.ceil((countdownEnds - Date.now()) / 1000));
  els.countdownValue.textContent = seconds;
  els.countdownOverlay.classList.remove("hidden");
}

function onSquareClick(row, col) {
  if (state.snapshot?.phase !== "game" || !state.selectedPieceId) return;
  const from = Object.values(state.snapshot.pieces || {}).find((piece) => piece.id === state.selectedPieceId);
  if (!from) return;
  send("move", {
    pieceId: state.selectedPieceId,
    path: [
      { row: from.row, col: from.col },
      { row, col }
    ]
  });
}

function syncRulesForm() {
  const rules = state.snapshot?.rules;
  if (!rules) return;
  const map = {
    mandatoryCapture: rules.mandatoryCapture,
    allowMultiCapture: rules.allowMultiCapture,
    requireMultiCapture: rules.requireMultiCapture,
    menCanCaptureBackward: rules.menCanCaptureBackward,
    kingsMoveMultipleSquares: rules.kingsMoveMultipleSquares,
    kingsMustLandDirectlyAfterCapture: rules.kingsMustLandDirectlyAfterCapture,
    kingsMustCapture: rules.kingsMustCapture,
    kingsCanMultiCapture: rules.kingsCanMultiCapture,
    kingsCanChangeDirectionDuringMultiCapture: rules.kingsCanChangeDirectionDuringMultiCapture,
  };
  for (const [key, value] of Object.entries(map)) {
    els.rulesForm.elements[key].checked = Boolean(value);
  }
  els.rulesForm.elements.missedCapturePenalty.value = String(rules.missedCapturePenalty);
  els.rulesForm.elements.missedCaptureTimePenaltySeconds.value = String(rules.missedCaptureTimePenaltySeconds);
  Array.from(els.rulesForm.elements).forEach((element) => {
    if ("disabled" in element) {
      element.disabled = !state.isHost;
    }
  });
}

function readDict(obj, side) {
  if (!obj) return null;
  if (side === "white") return obj.White ?? obj.white ?? null;
  return obj.Black ?? obj.black ?? null;
}

function normalizeSide(side) {
  if (side === 0 || side === "White" || side === "white") return "white";
  return "black";
}

function humanSide(side) {
  return normalizeSide(side) === "white" ? "Weiss" : "Schwarz";
}

function fmtTime(ms = 0) {
  const total = Math.max(0, Math.floor((ms ?? 0) / 1000));
  return `${String(Math.floor(total / 60)).padStart(2, "0")}:${String(total % 60).padStart(2, "0")}`;
}

function fmtStats(stats) {
  const best = stats.bestMoveTimeMs ? `${(stats.bestMoveTimeMs / 1000).toFixed(2)}s` : "-";
  return `Spiele ${stats.totalGames || 0} | Siege ${stats.wins || 0} | Niederlagen ${stats.losses || 0} | Bestzeit ${best}`;
}

function setBanner(text) {
  els.statusBanner.textContent = text;
}

async function loadPlayerStats() {
  if (!state.playerName) return;
  try {
    const response = await fetch(`/api/player?name=${encodeURIComponent(state.playerName)}`, { cache: "no-store" });
    const profile = await response.json();
    els.playerWinsValue.textContent = profile?.stats?.wins ?? 0;
  } catch {
    els.playerWinsValue.textContent = "0";
  }
}

async function loadOpenRooms() {
  if (state.currentScreen !== "room") return;
  try {
    const response = await fetch("/api/rooms", { cache: "no-store" });
    const rooms = await response.json();
    if (!rooms.length) {
      els.openRoomsList.innerHTML = "<p>Im Moment ist kein offener Raum vorhanden.</p>";
      return;
    }
    els.openRoomsList.innerHTML = rooms.map((room) => `
      <button type="button" class="room-entry" data-code="${room.roomCode}">
        <strong>${room.roomCode}</strong>
        <span>Host: ${room.hostName}</span>
        <span>${room.playerCount}/2 Spieler</span>
      </button>
    `).join("");
    els.openRoomsList.querySelectorAll(".room-entry").forEach((button) => {
      button.addEventListener("click", () => {
        els.roomCode.value = button.dataset.code;
      });
    });
  } catch {
    els.openRoomsList.innerHTML = "<p>Offene Raeume konnten gerade nicht geladen werden.</p>";
  }
}

function startRoomRefresh() {
  stopRoomRefresh();
  loadOpenRooms();
  state.roomsRefreshId = setInterval(loadOpenRooms, 5000);
}

function stopRoomRefresh() {
  if (state.roomsRefreshId) {
    clearInterval(state.roomsRefreshId);
    state.roomsRefreshId = null;
  }
}

setInterval(() => {
  if (state.snapshot) {
    renderGameHud();
    renderCountdown();
  }
}, 250);
