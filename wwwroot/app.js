const socket = new WebSocket(`ws://${location.host}/ws`);

const state = {
  connected: false,
  roomCode: "",
  mySide: null,
  isHost: false,
  snapshot: null,
  selectedPieceId: null,
  targets: [],
};

const els = {
  statusBanner: document.querySelector("#statusBanner"),
  playerName: document.querySelector("#playerName"),
  roomCode: document.querySelector("#roomCode"),
  createRoomBtn: document.querySelector("#createRoomBtn"),
  joinRoomBtn: document.querySelector("#joinRoomBtn"),
  roomCodeValue: document.querySelector("#roomCodeValue"),
  hostNameValue: document.querySelector("#hostNameValue"),
  playerSideValue: document.querySelector("#playerSideValue"),
  gameStatusValue: document.querySelector("#gameStatusValue"),
  board: document.querySelector("#board"),
  rulesForm: document.querySelector("#rulesForm"),
  pieceColor: document.querySelector("#pieceColor"),
  kingColor: document.querySelector("#kingColor"),
  whiteName: document.querySelector("#whiteName"),
  blackName: document.querySelector("#blackName"),
  whiteTime: document.querySelector("#whiteTime"),
  blackTime: document.querySelector("#blackTime"),
  whiteStats: document.querySelector("#whiteStats"),
  blackStats: document.querySelector("#blackStats"),
  whiteCard: document.querySelector("#whiteCard"),
  blackCard: document.querySelector("#blackCard"),
};

function send(type, payload) {
  socket.send(JSON.stringify({ type, payload }));
}

socket.addEventListener("open", () => {
  state.connected = true;
  setBanner("Verbunden. Raum erstellen oder beitreten.");
});

socket.addEventListener("close", () => {
  state.connected = false;
  setBanner("Verbindung getrennt.");
});

socket.addEventListener("message", (event) => {
  const { type, payload } = JSON.parse(event.data);
  if (type === "roomJoined") {
    state.roomCode = payload.roomCode;
    state.mySide = payload.side;
    state.isHost = payload.isHost;
    renderRoomMeta();
  } else if (type === "snapshot") {
    state.snapshot = payload;
    state.selectedPieceId = payload.selectedPieceId;
    state.targets = payload.forcedDestinations || [];
    renderAll();
  } else if (type === "error") {
    setBanner(payload.message);
  } else if (type === "toast") {
    setBanner(payload.message);
  }
});

els.createRoomBtn.addEventListener("click", () => {
  send("createRoom", { name: els.playerName.value.trim() });
});

els.joinRoomBtn.addEventListener("click", () => {
  send("joinRoom", { name: els.playerName.value.trim(), roomCode: els.roomCode.value.trim().toUpperCase() });
});

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
      kingsMustCapture: form.get("kingsMustCapture") === "on",
      kingsCanMultiCapture: form.get("kingsCanMultiCapture") === "on",
      kingsCanChangeDirectionDuringMultiCapture: form.get("kingsCanChangeDirectionDuringMultiCapture") === "on",
      missedCapturePenalty: Number(form.get("missedCapturePenalty")),
      missedCaptureTimePenaltySeconds: Number(form.get("missedCaptureTimePenaltySeconds")),
    }
  });
});

els.pieceColor.addEventListener("input", applyLocalColors);
els.kingColor.addEventListener("input", applyLocalColors);

function renderAll() {
  renderRoomMeta();
  renderCards();
  renderBoard();
  syncRulesForm();
}

function renderRoomMeta() {
  els.roomCodeValue.textContent = state.roomCode || "-";
  els.hostNameValue.textContent = state.snapshot?.hostName || "-";
  els.playerSideValue.textContent = state.mySide || "-";
  els.gameStatusValue.textContent = state.snapshot?.statusMessage || "Warte auf Verbindung";
  setBanner(state.snapshot?.statusMessage || "Bereit.");
}

function renderCards() {
  const snap = state.snapshot;
  if (!snap) return;
  const whiteStats = snap.stats.White || snap.stats.white;
  const blackStats = snap.stats.Black || snap.stats.black;
  els.whiteName.textContent = snap.players.White || snap.players.white || "-";
  els.blackName.textContent = snap.players.Black || snap.players.black || "-";
  els.whiteTime.textContent = fmtTime(snap.remainingTurnMs.White ?? snap.remainingTurnMs.white);
  els.blackTime.textContent = fmtTime(snap.remainingTurnMs.Black ?? snap.remainingTurnMs.black);
  els.whiteStats.textContent = fmtStats(whiteStats);
  els.blackStats.textContent = fmtStats(blackStats);
  const turn = snap.currentTurn;
  els.whiteCard.classList.toggle("active", turn === "White" || turn === 0);
  els.blackCard.classList.toggle("active", turn === "Black" || turn === 1);
}

function renderBoard() {
  els.board.innerHTML = "";
  const snap = state.snapshot;
  const pieces = snap?.pieces ? Object.values(snap.pieces) : [];

  for (let row = 0; row < 10; row++) {
    for (let col = 0; col < 10; col++) {
      const square = document.createElement("button");
      square.className = `square ${(row + col) % 2 === 0 ? "light" : "dark"}`;
      square.type = "button";
      const target = state.targets.some((pos) => pos.row === row && pos.col === col);
      if (target) square.classList.add("target");
      square.addEventListener("click", () => onSquareClick(row, col));

      const piece = pieces.find((item) => item.row === row && item.col === col);
      if (piece) {
        const el = document.createElement("div");
        const isMine = normalizeSide(piece.side) === normalizeSide(state.mySide);
        el.className = `piece ${normalizeSide(piece.side)} ${piece.isKing ? "king" : ""}`;
        if (piece.id === state.selectedPieceId) el.classList.add("selected");
        el.dataset.symbol = piece.isKing ? "D" : "";
        el.addEventListener("click", (event) => {
          event.stopPropagation();
          if (isMine) {
            send("selectPiece", { pieceId: piece.id });
          }
        });
        square.appendChild(el);
      }

      els.board.appendChild(square);
    }
  }
}

function onSquareClick(row, col) {
  if (!state.selectedPieceId) return;
  const from = findSelectedPiece();
  if (!from) return;
  send("move", {
    pieceId: state.selectedPieceId,
    path: [
      { row: from.row, col: from.col },
      { row, col }
    ]
  });
}

function findSelectedPiece() {
  const pieces = Object.values(state.snapshot?.pieces || {});
  return pieces.find((piece) => piece.id === state.selectedPieceId);
}

function syncRulesForm() {
  if (!state.snapshot?.rules) return;
  const rules = state.snapshot.rules;
  const map = {
    mandatoryCapture: rules.mandatoryCapture,
    allowMultiCapture: rules.allowMultiCapture,
    requireMultiCapture: rules.requireMultiCapture,
    menCanCaptureBackward: rules.menCanCaptureBackward,
    kingsMoveMultipleSquares: rules.kingsMoveMultipleSquares,
    kingsMustCapture: rules.kingsMustCapture,
    kingsCanMultiCapture: rules.kingsCanMultiCapture,
    kingsCanChangeDirectionDuringMultiCapture: rules.kingsCanChangeDirectionDuringMultiCapture,
  };
  for (const [key, value] of Object.entries(map)) {
    els.rulesForm.elements[key].checked = Boolean(value);
  }
  els.rulesForm.elements.missedCapturePenalty.value = String(rules.missedCapturePenalty);
  els.rulesForm.elements.missedCaptureTimePenaltySeconds.value = String(rules.missedCaptureTimePenaltySeconds);
  Array.from(els.rulesForm.elements).forEach((el) => {
    if ("disabled" in el) el.disabled = !state.isHost;
  });
}

function fmtTime(ms = 0) {
  const totalSeconds = Math.max(0, Math.floor(ms / 1000));
  const minutes = String(Math.floor(totalSeconds / 60)).padStart(2, "0");
  const seconds = String(totalSeconds % 60).padStart(2, "0");
  return `${minutes}:${seconds}`;
}

function fmtStats(stats = {}) {
  const best = stats.bestMoveTimeMs ? `${(stats.bestMoveTimeMs / 1000).toFixed(2)}s` : "-";
  return `Spiele ${stats.totalGames || 0} | Siege ${stats.wins || 0} | Niederlagen ${stats.losses || 0} | Bestzeit ${best}`;
}

function normalizeSide(side) {
  if (side === 0 || side === "White") return "white";
  return "black";
}

function applyLocalColors() {
  document.documentElement.style.setProperty("--my-piece", els.pieceColor.value);
  document.documentElement.style.setProperty("--my-king", els.kingColor.value);
}

function setBanner(text) {
  els.statusBanner.textContent = text;
}

applyLocalColors();
setInterval(() => {
  if (state.snapshot) renderCards();
}, 300);
