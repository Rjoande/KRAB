# KRAB-9000 — Hub di progetto per Claude Code

Mod KSP: estensione del KAL-1000 con editor a nodi (sorgenti multiple → operatori →
output verso le parti). Documento di riferimento: [KRAB-9000_design_doc.md](KRAB-9000_design_doc.md).

**Convenzioni di lingua (decise dall'utente, 2026-07-06):**
- chat e appunti interni (`notes/`, CLAUDE.md): **italiano**;
- commenti nel codice e nei .cfg: **inglese**;
- ogni stringa visibile al giocatore passa dal sistema di localizzazione KSP
  (`#LOC_KRAB_*` in `Localization/en-us.cfg`, master inglese durante lo sviluppo;
  traduzioni a lavoro finito). Mai stringhe hardcoded nella UI.

## Stato (aggiornare a ogni sessione)

- **2026-07-25 (42)** — **Riorganizzazione a doppia radice** (utente): spostati
  `Config/`, `Localization/`, `Parts/`, `Plugins/`, `Textures/`, `README.md` (e
  di conseguenza anche `CHANGELOG.md`, che li aveva raggiunti) dentro una nuova
  sotto-cartella `GameData/KRAB/GameData/KRAB/`. La cartella di questo hub
  (`GameData/KRAB/`, radice) ora rispecchia il layout del repo GitHub;
  `GameData/KRAB/GameData/KRAB/` è la mod vera e propria, quella da installare
  e da impacchettare in release — e siccome è comunque dentro l'installazione
  KSP di sviluppo (scansione ricorsiva di `GameData/`), si carica in gioco
  senza alcun passaggio di copia separato. Aggiornato **solo** il percorso di
  destinazione della DLL nel csproj (`DeployToGameData`): da `../Plugins/` a
  `../GameData/KRAB/Plugins/`. Nessun'altra modifica necessaria — i riferimenti
  `HintPath` verso `KSP_x64_Data/Managed` restano corretti perché `src/` non si
  è spostata (resta 3 livelli sotto la vera radice KSP). Verificato con una
  build pulita che la DLL atterra nella nuova posizione. Vedi la sezione
  "Layout della cartella" qui sotto, riscritta per riflettere i due livelli.
- **2026-07-24 (41)** — **Evidenziazione blu scuro persistente sulla parte
  bersaglio** dell'output attualmente selezionato (scheda aperta), stesso
  meccanismo già in uso su KRILL (`KrillWindow.cs`, stesso colore
  `(0.18, 0.35, 0.85)`). Nuovi: `highlightedPart` + `ApplyTargetHighlight`/
  `ClearTargetHighlight`/`UpdateTargetHighlight`/`ResolveActiveOutputTargetPart`
  in `KrabEditorWindow.cs`; chiamata a `UpdateTargetHighlight()` a ogni
  `RebuildContent()` (dopo `CollectOutputs()`, così `activeOutputId` è già
  aggiornato); pulizia in `OnDestroy()`; nuovo handler `GameEvents.onVesselChange`
  (KRAB non ne aveva ancora uno) che ri-valida l'evidenziazione al cambio nave
  attiva. **Riprodotto anche l'accorgimento non ovvio di KRILL**: l'hover
  ciano transitorio del picking (già esistente in KRAB) ora, quando smette di
  puntare una parte che è ANCHE il bersaglio evidenziato in blu, ripristina il
  blu invece di azzerare tutto — stesso fix già applicato in `ClearPickerState`
  e nel toggle hover di `HandlePartPicking`. **Bug trovato in KRILL** (non in
  KRAB) mentre verificavo questo meccanismo per il porting: un'evidenziazione
  ciano può restare bloccata per sempre su una parte se si preme Esc mentre il
  tasto del mouse è ancora premuto tra il mouse-down e il mouse-up del
  picking — causa: la versione di KRILL della transizione mouse-down→
  `pendingPickPart` ha perso la riga `hoverPart.SetHighlightDefault()` che
  KRAB (l'originale da cui è stato portato) invece ha. Report completo in
  `GameData/KRILL/notes/bug-report-picker-highlight-stuck.md` (non corretto,
  solo segnalato — fuori mandato per questa sessione KRAB). Build pulita, DLL
  deployata. **Da testare in gioco**: evidenziazione al cambio scheda,
  interazione con l'hover ciano durante un nuovo picking sullo stesso
  bersaglio, comportamento al cambio nave attiva, pulizia alla chiusura
  finestra.
- **2026-07-24 (40)** — Troncamento a 15 caratteri (con `…`) per nome parte e
  nome campo/azione nella descrizione del bersaglio (`ResolveTargetLabel`),
  applicato indipendentemente ai due — un nome lungo da un lato non spinge
  l'altro fuori vista. Build pulita, DLL deployata. Resta v0.1.0.
- **2026-07-23 (39)** — Due rifiniture pre-release (restano in v0.1.0, nessuna
  voce di Changelog per richiesta esplicita). (1) **Icone flip vere**:
  `icon_flipV.png`/`icon_flipH.png` fornite dall'utente, verificate
  formalmente (128×128, RGBA, bianco puro, bordi con antialiasing — stesso
  esito PASS del primo lotto) e collegate al posto dei glifi ↕/↔ nel footer
  della finestra curva. (2) **Messaggi di validazione localizzati**: la
  striscia di validazione dell'editor mostrava `issue.message` grezzo in
  inglese — un TODO segnato fin dal commento originale di `ValidationIssue`
  ("the editor UI milestone will map code to #LOC_KRAB_ keys") mai chiuso.
  Fix: `ValidationIssue` ora porta anche `args` (le parti dinamiche: id nodo,
  sottotipo, numero di porta, conteggi, ecc.) accanto al `message` inglese
  (che resta per il log, invariato — mai localizzato, per convenzione);
  nuovo `LocalizedText()` che mappa `code` → `#LOC_KRAB_issue_<code>` e
  formatta con `args`, con fallback al messaggio inglese se manca la chiave.
  Il codice `nodeUnknownSubtype` split in due varianti (`nodeWrongKind` per
  "sottotipo esiste ma nel kind sbagliato" vs `nodeUnknownSubtype` per
  "sottotipo mai visto") — verificato che `KrabGraphSelfTest.cs` continua a
  passare invariato (il suo caso di test, subtype "FluxCapacitor", cade nel
  ramo davvero-sconosciuto, stesso codice di prima). 17 nuove chiavi
  `issue_*` in entrambe le lingue. Copertura verificata: 193/193 chiavi in
  parità EN/IT, zero mancanti, zero duplicati. Build pulita, DLL deployata.
  **Da testare in gioco**: la striscia di validazione in italiano — la
  maggior parte dei codici richiede un grafo corrotto a mano per attivarsi,
  ma `tooFewInputs`/`portUnconnected`/`noActiveOutput`/`nodeOrphan` sono
  raggiungibili anche dall'editing normale (es. rimuovere termini di un
  And/Or fino a scendere sotto il minimo).
- **2026-07-22 (38)** — **Bug di posizione dei tooltip corretto.** Erano
  ancorati all'angolo in basso a sinistra del canvas (anchor `(0,0)`), ma
  `ScreenPointToLocalPointInRectangle` restituisce un punto relativo al
  **pivot** del canvas (di norma il centro, non l'angolo) — stesso tipo di
  errore già visto nella finestra curva (voce 28). Risultato: il tooltip
  appariva spostato di circa mezzo schermo dal controllo reale (in-game
  report, screenshot). Fix: ancorato il popup al pivot del canvas stesso
  (`canvasRect.pivot`) invece che a `(0,0)`, stesso sistema di riferimento
  usato da `ScreenPointToLocalPointInRectangle`. Build pulita, DLL deployata.
  **Da riverificare**: posizione corretta del tooltip su vari controlli.
- **2026-07-22 (37)** — **Tooltip al passaggio del mouse**, livello 1 (icone) +
  livello 2 (nomi operatori nell'albero), come da proposta approvata
  dall'utente. Nuovo meccanismo in `KrabUi.cs`: `Tooltip(GameObject, locKey)` —
  aggiunge il componente al controllo esistente (non un overlay separato
  sopra il bottone: avrebbe intercettato hover/click prima del `Button`
  sottostante, spegnendo la sua evidenziazione al passaggio del mouse) e ne
  forza `raycastTarget = true` se non lo era già (le `Label` semplici — es. i
  nomi degli operatori — lo hanno `false` di default per scelta di design;
  qui è un'eccezione mirata solo al controllo che la richiede, non un
  cambiamento allo stile condiviso). Dopo ~0.5s di hover crea un piccolo
  pannello (stile chip esistente) ancorato appena sotto il controllo — non al
  cursore, niente jitter — con testo a capo automatico; si chiude e si
  distrugge all'uscita del mouse o se il controllo stesso viene distrutto
  (`OnDisable`, stesso principio di `FocusLock`). Nessuno stato condiviso tra
  le due finestre. Collegati: undo/redo/copy/paste/curve, ✕ elimina
  uscita/termine, ↻ cicla operatore, ↕/↔ flip nella finestra curva, e il nome
  di ogni operatore/termine nell'albero (chiavi `tip_node_*`, una per
  ciascuna delle 22 voci del catalogo nodi, stesso contenuto già scritto come
  commenti per i traduttori alla voce `node_*`). 32 nuove chiavi di
  localizzazione, copertura verificata (22/22 `tip_node_*` contro `node_*`,
  zero chiavi mancanti, zero duplicati). Build pulita, DLL deployata.
  **Prossimo** (se richiesto): livello 3 — abbreviazioni dei parametri
  (`thr`/`hys`/`τ`/`w`/`s`/`×`/`/s`) e intestazioni di famiglia nel picker
  sorgenti, deliberatamente escluse da questo giro per tenerlo gestibile.
- **2026-07-21 (36)** — **Flip verticale/orizzontale nella finestra curva.**
  `FlipVertical`: rispecchia il valore di ogni keyframe attorno al centro del
  range (`rangeMin+rangeMax-value`), il tempo non cambia quindi l'ordine
  resta identico (il punto selezionato, se c'è, resta lo stesso). `FlipHorizontal`:
  rispecchia il tempo attorno al centro del dominio, il che **inverte
  l'ordine** delle keyframe — l'array viene ricostruito a ritroso per restare
  ascendente, e l'indice del punto selezionato viene rimappato
  (`length-1-indice`) così resta associato allo stesso punto fisico invece di
  saltare su un altro. Entrambi ricalcolano le tangenti da zero
  (`SmoothAll`) dopo la trasformazione, niente gestione manuale delle
  tangenti. Bottoni nel footer della finestra curva (glifi `↕`/`↔`, blocco
  Arrows già validato altrove in questa UI — non icone PNG custom, il set
  disegnato dall'utente non copriva il flip). Build pulita, DLL deployata.
  **Prossimo**: tooltip su operatori/bottoni nei due editor (richiesta
  utente, da affrontare separatamente per lo scoping — nuovo meccanismo,
  `KrabUi` non ne ha ancora uno).
- **2026-07-20 (35)** — Ripristinato il glifo `✕` nella titlebar di entrambe
  le finestre (editor principale e finestra curva) al posto di `icon_close`
  (non convinceva visivamente, feedback utente). `icon_close.png` resta in
  `Textures/`, semplicemente non più referenziata nel codice. Build pulita,
  DLL deployata.
- **2026-07-20 (34)** — Due rifiniture di layout dopo il test delle icone.
  (1) **Copy/Paste "sperdute"**: la riga non aveva alcun riferimento di
  allineamento nella colonna destra della scheda bersaglio — aggiunto lo
  stesso spaziatore elastico già usato dalla riga VALUE/✕ sottostante, così
  le due righe ora si allineano entrambe a destra. (2) **Il ✕ elimina termine
  spostava la colonna dei valori**: essendo aggiunto solo condizionalmente
  dopo lo spaziatore elastico, "rubava" spazio fisso alla riga solo quando
  presente, spingendo il blocco valore ~18-24px più a sinistra sulle righe
  rimovibili rispetto a quelle non rimovibili — stessa causa (slot non a
  larghezza fissa) del vecchio bug del bottone ⟳ (voce 16), risolta con lo
  stesso rimedio: uno slot fisso da 18px sempre riservato dopo il valore, con
  dentro il ✕ quando il termine è rimovibile o vuoto (solo spaziatura)
  quando non lo è — identico al `CycleSpacer` già presente poco sopra nella
  stessa riga. Nessun cambio di comportamento, solo allineamento. Build
  pulita, DLL deployata.
- **2026-07-19 (33)** — **Icone custom collegate alla UI**, dopo verifica
  formale delle 6 PNG fornite dall'utente (`notes/icon-spec.md`): tutte
  **PASS** — 128×128 (l'utente ha deciso di rendere quadrata anche `curve`,
  aggiornando la specifica), PNG-32 RGBA a 8 bit, **ogni pixel visibile bianco
  puro** (0% di deviazione cromatica sui pixel non trasparenti — il requisito
  critico per la tinta a runtime), bordi con antialiasing regolare (3-6% pixel
  ad alpha parziale). Nuovo `KrabUi.ImageIconButton` (Textures/icon_&lt;nome&gt;.png
  via `GameDatabase`, **non** un asset bundle — resta un semplice PNG indicizzato
  automaticamente, coerente con l'architettura "UI a codice"; Sprite
  cachata per nome così il caricamento avviene una sola volta a sessione,
  non a ogni rebuild). Sostituiti: undo/redo (erano già icone a glifo, ora
  texture), copy/paste e curve (erano bottoni testuali, ora icone quadrate).
  **Close**: era già presente come `[x]` nella cornice superiore di
  **entrambe** le finestre (editor principale e finestra curva) — non andava
  aggiunto, solo restilizzato con la nuova icona; rimosso il bottone "Close"
  ridondante in fondo a entrambe le finestre (il footer dell'editor principale
  ora mostra solo lo stato del grafo; il footer della finestra curva solo
  "Reset to linear"). Rimosse 4 chiavi di localizzazione rimaste orfane dopo
  la sostituzione (`ui_close`, `ui_copy`, `ui_paste`, `ui_curve` — le icone
  non hanno etichetta testuale e KrabUi non ha ancora un sistema di tooltip,
  quindi quelle parole non compaiono più da nessuna parte; da reintrodurre se
  in futuro arriva un sistema di tooltip). Build pulita, DLL deployata,
  copertura localizzazione verificata (zero chiavi mancanti, zero duplicati).
  **Nota di usabilità, non bloccante**: le icone da sole (senza tooltip) sono
  meno autoesplicative delle vecchie etichette testuali per chi apre l'editor
  per la prima volta — da tenere presente se in futuro si aggiunge un sistema
  di tooltip. **Da testare in gioco**: caricamento corretto delle texture
  (nessun quadrato vuoto/pieno per icona non trovata), tinta corretta nei vari
  stati (copy/paste/curve spenti quando non applicabili), il nuovo `[x]`
  chiude entrambe le finestre.
- **2026-07-18 (32)** — **Specifica icone scritta, nessun codice toccato.**
  Richiesta utente: sostituire undo/redo/copy/paste (oggi glifi/testo) con
  icone quadrate dedicate, `curve` con un'icona rettangolare, e il bottone
  "Close" in fondo alla finestra principale con la classica `[x]` in cornice
  (come già in `KrabCurveWindow`). Le icone le disegna l'utente; scritta la
  specifica completa in `notes/icon-spec.md` (formato PNG-32, silhouette
  **bianca pura su trasparente** — vincolo critico: i bottoni-icona esistenti
  tingono il glifo a runtime in base allo stato, `Image.color` moltiplica il
  pixel quindi solo il bianco puro dà una tinta esatta; tele 128×128 per le
  quadrate, 144×96 per curva; cartella nuova `Textures/`, prima volta che il
  progetto usa asset immagine invece di UI puramente a codice — resta
  comunque un semplice PNG via `GameDatabase`, non un asset bundle, coerente
  con la decisione architetturale esistente). Confermato all'utente che KSP
  non ha un'icona di chiusura stock facilmente referenziabile data
  l'architettura "UI tutta a codice" del progetto — icona custom anche per
  `close`, coerente con le altre cinque. **Prossimo**: quando l'utente
  fornisce i 6 PNG in `Textures/`, verifica formale (esistenza, formato,
  alpha, dimensioni, colore bianco puro) **prima** di collegarli alla UI.
- **2026-07-18 (31)** — Due richieste sull'editor principale, da testare insieme.
  (1) **Schede di uscita scorrevoli**: oltre ~4 uscite le schede si
  schiacciavano fino a diventare illeggibili (un `HorizontalLayoutGroup`
  semplice comprime i figli quando la loro larghezza totale supera la riga).
  Nuovo `KrabUi.HScrollList` (analogo orizzontale di `ScrollList`, stesso
  trucco: contenuto a dimensione libera dentro una vista mascherata a
  larghezza fissa, così i figli non vengono mai schiacciati) usato in
  `BuildTabs` per la sola striscia di schede; i bottoni "+ Axis output"/
  "+ Trigger" restano fuori, sempre visibili a destra. La rotellina del mouse
  su un'area orizzontale-soltanto non scorre da sola in Unity (mappa solo
  l'asse verticale): aggiunto `HorizontalWheelScroll`, un piccolo
  `IScrollHandler` che rimappa il delta verticale della rotellina sull'asse
  orizzontale dello `ScrollRect`. Il quadro sottostante (bersaglio, albero,
  simulatore) non è stato toccato. (2) **Copia/incolla dell'intero
  sottoalbero ingressi/operatori di un'uscita**, per replicarlo su un'altra
  scheda: due bottoni "Copy"/"Paste" nella scheda bersaglio (in alto a
  destra). `KrabGraphEdits.CopySubtree` risale dal nodo collegato alla porta 0
  raccogliendo l'intero sottoalbero a monte (nodi, link e DEFAULT interni) e
  lo serializza in un ConfigNode-stringa (id originali preservati nel testo);
  `PasteSubtree` clona il tutto con **id nuovi di zecca** (mai condivisi con
  l'originale — incollare due volte produce due copie indipendenti, non un
  riuso/fan-out) e lo aggancia alla porta 0 dell'uscita bersaglio, rimpiazzando
  quanto c'era prima (stesso `ClearPort` già usato dalle altre operazioni
  "Replace*"). Appunti di implementazione: gli appunti (`subtreeClipboard`)
  vivono in un campo **statico** di `KrabEditorWindow` (come `current`),
  quindi sopravvivono a chiusura/riapertura della finestra ma non a un riavvio
  di KSP; "Copy" non passa da `Mutate` (non tocca il grafo), "Paste" sì (serve
  l'undo) mentre risulta un no-op silenzioso — niente passo di undo spurio —
  se gli appunti sono vuoti. Aggiunte 2 chiavi di localizzazione
  (`ui_copy`/`ui_paste`), verificata copertura completa. Build pulita, DLL
  deployata. **Da testare**: scorrimento schede con rotellina oltre 4-5
  uscite; copia da un'uscita e incolla su un'altra (stessa combinazione
  visibile su entrambe, indipendenti l'una dall'altra); incolla con appunti
  vuoti (nessun effetto, nessuna voce di undo spuria); Copy/Paste con
  Undo/Redo in sequenza.
- **2026-07-17 (30)** — Pulizia dei due punti residui dal registro + supporto
  traduzioni. (1) Rimossi i due `Debug.LogFormat("[KRAB] pick: ...")` in
  `HandlePartPicking` (bug 3.5 confermato risolto da tempo, log non più
  necessario). (2) `Test/GraphSample.cfg`: trovato già rinominato
  `GraphSample.cfg.txt` da una sessione precedente (quindi già inerte, MM non
  lo legge più) — solo il registro qui sotto era rimasto disallineato dalla
  realtà; corretto. (3) **`Localization/en-us.cfg`: aggiunta una riga di
  commento sopra ciascuna delle ~90 chiavi**, a supporto delle traduzioni
  future — dice dove la stringa compare in gioco e, per le chiavi con
  `<<1>>`/`<<2>>`, cosa ci va sostituito. Verificata copertura chiavi
  usate-vs-definite dopo la modifica: nessuna chiave persa, nessun duplicato.
  Build pulita, DLL deployata.
- **2026-07-17 (29)** — **M4 confermata funzionante** (dopo il punto 28, restava un
  solo bug minore) + 3 rifiniture richieste sull'editor principale.
  **Bug del trascinamento punti curva**: si spostavano di ~0.01 per "afferrata",
  serviva ripetere il gesto molte volte. Causa: `DragPoint` chiamava
  `RedrawGraph()` a ogni frame di trascinamento, che **distrugge e ricrea** tutti
  i pallini — incluso quello che l'EventSystem di Unity sta trascinando in quel
  momento. Un GameObject distrutto smette silenziosamente di ricevere gli
  `OnDrag` successivi finché il tasto resta premuto: da cui un solo passo minimo
  applicato per gesto. Fix: nuovo `UpdateLiveDrag(index)` che sposta **in place**
  solo il pallino trascinato e ridisegna la linea, senza toccare GameObject;
  `RedrawGraph()` resta riservato a costruzione iniziale e aggiunte/rimozioni
  reali di punti.
  **Rifiniture richieste**: (1) le schede di uscita sono ora **rinominabili** —
  nuovo campo "name" nella scheda bersaglio (parametro `label` sul nodo
  Output, facoltativo: se vuoto torna al nome auto-rilevato); utile con più
  uscite dello stesso tipo affiancate. (2) La descrizione del bersaglio ora
  preferisce **"Titolo parte - Nome GUI del campo/azione"** (es. "Snodo
  alligatore G-01L - Angolo obiettivo") invece del nome tecnico grezzo
  (`targetAngle`), risolvendo dal vivo `BaseAxisField.guiName` o
  `BaseAction.guiName` quando la parte è raggiungibile; **ricade sul nome
  grezzo persistito** se non risolvibile al momento (nuovo `ResolveTargetLabel`
  + `TryResolveAction`, simmetrico a `TryResolveAxisField` già esistente).
  (3) Le etichette di Remap ora leggono **"in (min-max)"**/**"out (min-max)"**
  invece di "in"/"→ out", per chiarire che i due campi affiancati sono min e
  max. Aggiunta 1 chiave di localizzazione (`ui_tabName`). Build pulita, DLL
  deployata.
- **2026-07-15 (28)** — **Secondo giro di correzioni M4** dopo il retest del punto 27.
  (a) **Punti e cursore ancora spostati** (verso il centro della finestra, non più
  del riquadro): il fix precedente aveva corretto `pivot`/`sizeDelta` di
  `pointsHost` e `cursorMark`, ma **non `anchorMin`/`anchorMax`**, mai impostati
  esplicitamente su questi due oggetti — restavano quindi ancora all'ancoraggio
  di default di un RectTransform appena creato, non al punto (0,0) [angolo in
  basso a sinistra dell'area] su cui si basa tutta la matematica della finestra.
  Fix: `anchorMin = anchorMax = Vector2.zero` esplicito su entrambi (oltre a
  `anchoredPosition = Vector2.zero` come base di partenza esplicita). Lezione:
  ogni RectTransform creato "a mano" in questa finestra deve avere **tutti e
  quattro** i campi (anchorMin, anchorMax, pivot, sizeDelta) impostati
  esplicitamente, mai lasciati al default implicito di Unity — un solo campo
  lasciato implicito rompe la matematica in modo silenzioso. (b) **Undo/Redo
  chiudeva la finestra curva a ogni pressione**: il fix del punto 27 chiudeva
  la finestra **incondizionatamente** a ogni Undo/Redo (per evitare di scrivere
  su un nodo ormai fuori dal grafo dopo la sostituzione completa del `KrabGraph`),
  ma questo vanificava lo scopo di guardare l'effetto dell'undo sulla curva
  stessa. Fix: nuovo `KrabCurveWindow.SyncAfterUndoRedo()` — dopo un
  Undo/Redo si ri-aggancia al nodo con lo stesso id nel grafo appena ricaricato
  (gli id sono stabili, per design) e ricarica la curva da lì, **senza
  richiudersi**; si chiude solo se quel nodo (o la sua curva) non esiste più
  davvero nel grafo ripristinato — nel qual caso evita anche di seminare
  automaticamente una nuova curva lineare (che avrebbe generato un nuovo
  snapshot subito dopo l'undo dell'utente, vanificandolo). Build pulita, DLL
  deployata. **Da riverificare**: fasi 1, 2 e 6 di `notes/test-editor-m4.md`.
- **2026-07-15 (27)** — **Primo test in gioco di M4: 3 problemi trovati e corretti.**
  (a) **Curva disegnata fuori dal riquadro** (linea che sconfinava in alto a destra):
  `KrabCurveLine`'s RectTransform viene disteso (`Stretch`) sull'area, ma il suo
  **pivot** resta quello di default di Unity per un RectTransform nuovo (centro)
  — mai sovrascritto — quindi l'origine locale usata da `OnPopulateMesh` cadeva
  al centro del riquadro invece che nell'angolo in basso a sinistra. Fix:
  `lineRect.pivot = Vector2.zero` dopo `Stretch`. (b) **Punti non allineati al
  click, trascinamento che sembrava non funzionare**: il contenitore `pointsHost`
  ereditava le dimensioni di default di un RectTransform appena creato (100×100,
  pivot centrale) mai sovrascritte — introduceva un offset costante (pivot ×
  sizeDelta = 50,50) su ogni pallino rispetto alla posizione realmente cliccata;
  l'utente cliccava il pallino "vero" ma colpiva l'area vuota sottostante, che
  aggiungeva un nuovo punto anziché trascinare quello esistente. Fix:
  `pointsHostRect.pivot = pointsHostRect.sizeDelta = Vector2.zero`. (c) **Undo che
  cancellava troppo** (annullava fino alla creazione del Remap stesso): la
  finestra curva scrive nel nodo e ricompila (`NotifyGraphEdited`/`RecompileOnly`)
  ma non passava mai dallo snapshot-per-undo di `KrabEditorWindow.Mutate` — ogni
  modifica di curva restava "appesa" sull'ultimo vero snapshot preso nell'editor
  principale. Fix: `Mutate` ora delega la sola metà "snapshot" a un nuovo metodo
  `internal CaptureUndoSnapshot()`, che `KrabCurveWindow` chiama **una volta per
  gesto** (click per aggiungere, inizio trascinamento, rimozione, reset, conferma
  di un campo) — mai per frame di trascinamento, altrimenti ogni micro-movimento
  del mouse diventerebbe un proprio passo di undo. Build pulita, DLL deployata.
  **Da riverificare**: fasi 1, 2 e 6 di `notes/test-editor-m4.md` (le fasi 3-5
  erano già state confermate OK dal primo giro).
- **2026-07-15 (26)** — **M4 implementata (finestra curva dedicata per Remap).**
  Decisioni chiuse a inizio sessione (AskUserQuestion): curva come estensione
  opzionale di Remap (non un nuovo subtype), un'unica sessione di lavoro,
  cursore live via `EvalContext.simulate` fin da subito. Nuovi file:
  `src/UI/KrabCurveLine.cs` (polyline UGUI via `MaskableGraphic`/`OnPopulateMesh`,
  nessun asset esterno), `src/UI/KrabCurveWindow.cs` (finestra modeless: punti
  trascinabili con tangenti auto-smussate `AnimationCurve.SmoothTangents`,
  click su area vuota per aggiungere un punto, campi t/valore per il punto
  selezionato, "Reset to linear" per tornare ai 4 campi lineari). Estesi
  `KrabNode` (accessori a sotto-nodo `GetNode`/`SetNode`/`HasNode`/`RemoveNode`,
  necessari perché una curva è un ConfigNode annidato, non un valore piatto —
  il round-trip resta gratuito grazie a `CreateCopy()`), `RemapRuntime` (una
  curva presente sovrascrive interamente inMin/inMax/outMin/outMax),
  `ModuleKRABController` (`RecompileOnly()`: ricompila senza il giro completo
  di persistenza — usato a ogni frame di trascinamento, il salvataggio pieno
  avviene solo a fine gesto). **Scelta di design per il drag**: durante il
  trascinamento si evita di riordinare le keyframe (che `AnimationCurve` non
  gestisce automaticamente) clampando il tempo del punto trascinato tra i
  vicini immediati, invece di implementare un resort-and-retrack-index. Punto
  di robustezza trovato in revisione: se l'utente fa Undo/Redo (che sostituisce
  l'intero `KrabGraph`) mentre la finestra curva è aperta, il suo riferimento al
  nodo Remap diventerebbe silenziosamente orfano (le scritture non
  raggiungerebbero più il grafo vivo) — risolto chiudendo la finestra curva su
  Undo/Redo e alla chiusura dell'editor principale (`KrabCurveWindow.CloseAny()`).
  Il nodo con la finestra curva aperta resta evidenziato nell'albero
  (`KrabEditorWindow.NodeColor`, reso `internal` insieme a `NodeName` per
  condivisione tra le due finestre). Aggiunte 10 chiavi di localizzazione
  (`#LOC_KRAB_ui_curve*`). Build pulita (0 errori, 0 warning), DLL deployata.
  **Da testare in gioco**: procedura in `notes/test-editor-m4.md` (apertura/
  chiusura finestra, editing punti, persistenza, "Reset to linear", cursore
  live, robustezza su Undo/Redo e cambio scena).
- **2026-07-14 (25)** — **FASE 2 CHIUSA: tutti i test PASS** (secondo giro, dopo il
  fix di `FillRequiredPorts`). Unica segnalazione: i campi di **Remap**
  (`inMin`/`inMax`/`outMin`/`outMax`) non avevano etichette (solo numeri nudi
  separati da "→") — corretto aggiungendo "in" / "→ out" (coerente con lo stile
  già usato altrove: "thr"/"hys" ecc.). Build pulita, DLL deployata. **Si parte
  con la fase 3 (M4 — finestra curve dedicata)**, vedi `notes/prossimi-passi.md`
  §Fase 3 per le decisioni di design ancora aperte da chiudere prima del codice.
- **2026-07-10 (24)** — Primo giro di test fase 2: **A/B/D/E fallite** con lo stesso
  sintomo (porte a "0,0,0", nessun picker) — **causa trovata e corretta**:
  `CountInputPorts` su un nodo ad arità fissa restituisce sempre l'arità totale
  (es. 3 per Hold), mai "la prossima porta libera"; il vecchio auto-riempimento
  (via `AddTerm` in ciclo) collegava tutti i placeholder alla stessa porta
  fantasma fuori range. Fix: nuovo `FillRequiredPorts` con indici espliciti,
  usato da `AddSubgroup`/`ReplaceTermWithOperator` al posto di `AddTerm`. Chiarito
  (non un bug): "SlewRate" è mostrato in gioco come **"Rate Limit"** — solo
  nomenclatura. Chiuso un gap segnalato dall'utente: **displayName** (nome del
  KRAB) non aveva alcun controllo PAW editabile (KSP non ha un widget stock per
  editare stringhe) — aggiunto un campo di testo modificabile **nella titlebar
  della finestra editor** stessa (stesso principio del nome di KAL, che vive
  nella sua finestra custom). F: richiesta di più finestre editor aperte
  insieme — annotata in `notes/prossimi-passi.md`, non implementata (rimandata
  esplicitamente dall'utente). Build pulita, DLL deployata, localizzazione
  verificata. **Da riverificare**: sezioni A, B, D, E del protocollo fase 2.
- **2026-07-10 (23)** — **Lacuna funzionale reale trovata e chiusa mentre si
  scriveva il protocollo della fase 2**: l'editor non aveva **nessun modo** di
  creare nodi Remap, Derivative, SlewRate, Comparator o Hold — il ciclo ⟳ copre
  solo gli 8 combinatori N-ari, il picker sorgenti offriva solo sorgenti. Fix in
  due parti: (1) bottone **"+ Filter"** sui gruppi dinamici, per **aggiungere**
  uno dei 5 come nuovo termine (`KrabGraphEdits.InsertableFilters`,
  generalizzazione di `AddSubgroup` con parametro subtype); (2) nuova famiglia
  **"OPERATORS"** nel picker sorgenti (`ReplaceTermWithOperator`), che
  **sostituisce** qualunque foglia esistente con un operatore — necessaria per
  annidare un operatore ad arità fissa dentro la porta di un altro (es. Remap
  dentro SlewRate), cosa che "+ Filter" da solo non permette essendo i nodi ad
  arità fissa privi di bottoni "+Term/+Group/+Filter" propri. Scritto e
  corretto di conseguenza `notes/test-fase2-esteso.md` (protocollo fase 2, un
  solo craft per tutte le 8 sezioni A-H). Build pulita, DLL deployata,
  localizzazione verificata al 100%. Prossimo: eseguire il test in gioco.
- **2026-07-09 (22)** — **Fase 1 (pulizia) chiusa.** Decisioni utente: `src/Eval/*.cs.disable`
  restano disattivati (niente git = niente rete di sicurezza per eliminare); i 3
  mockup HTML archiviati in `notes/archive/`; gli aiuti di debug (self-test, bind
  servo/luce, `debugOutputValue`) **non vengono rimossi** ma diventano permanenti
  come rete di sicurezza per regressioni, nascosti dietro un nuovo **`debugMode`**
  (campo cfg-only sul MODULE della parte, mai un toggle PAW/Advanced Tweakables —
  `Fields[...].guiActive`/`Events[...].active` impostati in `OnStart` in base al
  suo valore). Aggiornati albero cartelle e registro in questo file. Build pulita,
  DLL deployata. Scritto il piano delle fasi 2 (test esteso) e 3 (M4) in
  `notes/prossimi-passi.md`. Prossimo: eseguire la fase 2.
- **2026-07-09 (21)** — **Bug 3.5 e 4.2 confermati risolti in gioco.** Pulizia e
  rifinitura: i tre file di `src/Eval/` (mai collegati) rinominati in
  `.cs.disable` — verificati zero riferimenti esterni prima di disattivarli, il
  progetto compila pulito. **Nota estetica corretta subito** (era piccola e ben
  definita): il VALUE della card bersaglio di un AxisOutput ora è **clampato
  all'intervallo reale del campo bersaglio** (softLimits se dichiarati, altrimenti
  min/max assoluto — stessa fonte che usa `AxisOutputRuntime` alla scrittura vera),
  invece di estrapolare linearmente oltre 0..1 quando un operatore a monte produce
  valori fuori range (causa del "442°" su un hinge 0-180°; in volo non succedeva
  nulla di male perché la scrittura reale era già clampata via `Mathf.InverseLerp`
  — solo l'anteprima mentiva). **Chiarita (non un bug)** la questione delle unità
  mancanti su alcuni VALUE: verificato sul decompilato che è puro dato di
  autoraggio per-campo (`authorityLimiter` dichiara `guiUnits = "%"` nel suo
  `[KSPField]`, `targetAngle` non dichiara alcun `guiUnits` nel suo
  `[KSPAxisField]`) — nessun meccanismo globale possibile, il codice già fa la
  cosa giusta quando l'informazione esiste. Build pulita, DLL deployata.
  Prossimo: da pianificare con l'utente (M4 curve, o altro).
- **2026-07-09 (20)** — Indagati a fondo i due bug lasciati aperti, **entrambi con
  causa verificata (non ipotesi) e fix applicato**:
  - **Bug 3.5** (il click del picker sposta la parte in VAB): il lock veniva
    rimosso al mouse-**down**, ma il tasto resta fisicamente premuto per i frame
    successivi — verificato che `EDITOR_PAD_PICK_PLACE` è già incluso in
    `ALLBUTCAMERAS`, quindi non era un flag mancante ma un problema di sequenza.
    Fix: il pick si conferma solo al mouse-**up** (`KrabEditorWindow.HandlePartPicking`,
    nuovo campo `pendingPickPart`), lock attivo per l'intera pressione. Aggiunto
    log diagnostico `[KRAB] pick: ...` (posizione parte + stato lock) per un
    secondo giro se servisse.
  - **Bug 4.2** (riuso segnali → "valore nullo", "non si aggiungono termini"):
    causa reale trovata leggendo il codice — `KrabEvaluator.Compile` rifiutava
    **l'intero grafo** se un solo gruppo era strutturalmente invalido (es. 0
    termini), congelando la telemetria ovunque, non solo nel punto rotto. Il
    motore già degrada con grazia i casi locali (porta scoperta→0, link
    penzolante→ignorato, ciclo→escluso dal topo-sort): bastava non rifiutare più
    l'intero grafo. Fix in `KrabEvaluator.Compile`: non rifiuta più su errori di
    validazione strutturali, logga comunque (`graph compiled with error(s)`).
    Corretto anche un rischio di crash non collegato ma trovato nello stesso punto
    (porta DEFAULT negativa → `IndexOutOfRangeException`, raggiungibile solo da
    ConfigNode scritto a mano, mai dalla nostra UI).
  - **Segnalazione (non risolta, fuori mandato)**: trovato un secondo
    `KrabEvaluator.cs` orfano in `src/Eval/` (+ `NodeEvaluators.cs`,
    `OutputBindings.cs`, ~1150 righe) — architettura alternativa mai collegata a
    `ModuleKRABController` (che usa solo `KRAB.Graph.Evaluation`). Codice morto,
    da decidere se rimuovere.
  Build pulita, DLL deployata. Protocollo di verifica in
  `notes/test-bug-3.5-4.2.md`. Prossimo: test in gioco di questi due fix, poi M4
  o la pulizia di `src/Eval/`, a scelta dell'utente.
- **2026-07-09 (19)** — Test M3 in gioco: fasi 1,2(2-4),3(1-4),5 **OK**. Corretti tutti
  i punti azionabili: **finestra a dimensione fissa** (albero e slider del simulatore
  ora in `KrabUi.ScrollList` a altezza costante — risolve sia lo scroll off-screen
  con molti termini sia la deriva di titlebar/undo-redo, stessa causa in grande del
  vecchio bug del bottone ↻); **chip unità → controllo segmentato** a selezione
  diretta; **simulatore: sempre SI + (unità scelta) tra parentesi** se diversa;
  **indicatore "target part missing"** (rosso) quando il persistentId salvato non
  risolve più sul craft corrente (segnalato, non auto-cancellato); **readout nelle
  unità reali del target** (es. gradi di un hinge invece di 0-1, via `guiUnits` del
  campo); **campo sample-rate nascosto in simulazione** (era inerte lì, causa la
  confusione "non fa nulla" — funzionale solo in volo). **Due punti lasciati aperti
  con analisi ma senza fix alla cieca** (dettaglio in `notes/test-editor-m3.md`):
  il click del picker sposta la parte in VAB (verificato: `EDITOR_PAD_PICK_PLACE` è
  già incluso in `ALLBUTCAMERAS`, quindi non è un flag mancante — ipotesi: evento
  Unity diretto sul collider, non intercettato da InputLockManager; cosmetico, il
  binding per persistentId resta valido); bug minore su riuso segnali quando il
  nodo riusato è l'unico termine del suo gruppo (traccia statica non conclusiva,
  serve repro più precisa). Aggiunta nota di design (non implementata): comandi di
  volo come bersaglio di output, rischio di loop, rimandata (`notes/catalogo-nodi.md`).
  Build pulita, DLL deployata. Prossimo: M4 (curve dedicate) o le due indagini aperte,
  a scelta dell'utente.
- **2026-07-09 (18)** — **M3 implementata** + fix leggibilità richiesti dall'utente
  (glifi icona scalati ~80% del bottone; strip errori e footer a 12px con colori
  pieni — mai retrocedere a 10px, era la riga meno leggibile della finestra).
  M3 (`KrabUnits.cs` nuovo, estensioni a `KrabGraphEdits`, picker in
  `KrabEditorWindow`): **picker sorgenti** a famiglie con vocabolari localizzati
  (i campi testo liberi channel/metric/group sono rimossi); **picker bersagli col
  gesto KAL** — "Pick target…" → click sulla parte in scena (`Mouse.HoveredPart`,
  evidenziazione ciano, solo stesso craft, Esc annulla, lock input in VAB) → lista
  campi asse o azioni della parte → bind; **chip unità** sui termini fisici
  (`displayUnit` persistito, conversione solo di visualizzazione, slider canonici);
  **riuso segnali (fan-out)** nel picker con guardia anti-ciclo (`IsDownstream`).
  **Da testare in gioco**: `notes/test-editor-m3.md` (priorità: gesto KAL in VAB,
  invarianza del canonico al cambio unità). Dopo il PASS della fase 3, rimuovere
  gli aiuti di debug bind (registro). Build eseguita e DLL deployata il 2026-07-09
  (0 errori, 0 warning); verificata la copertura completa delle 42 chiavi di
  vocabolario (canali/metriche/action group) contro gli array nel codice.
- **2026-07-06 (17)** — Test M2 in gioco (`notes/test-editor-m2.md`): **fasi 1,3,4,5,
  6,7 tutte OK**. Fase 2 (ciclo operatore) OK con 3 problemi minori, tutti corretti:
  (a) il glifo `⟳` (U+27F3, Supplemental Arrows-A) non è coperto dal font di sistema
  usato in gioco → sostituito con `↻` (U+21BB, stesso blocco Arrows di ↶/↷, che
  infatti già rendevano); (b) il bottone si spostava quando il nome dell'operatore
  cambiava lunghezza (era dopo il nome) → spostato prima, su slot a larghezza fissa;
  (c) **armonizzazione richiesta dall'utente**: un gruppo dinamico sceso sotto il
  proprio minimo di ingressi (es. And/Or a 1 termine dopo una rimozione) non era
  sbloccabile con ↻ (solo riaggiungendo un termine), mentre un gruppo ad arità fissa
  (Gated Blend ecc.) lo era già — `CycleOperator` ora salta al primo operatore
  compatibile per l'arità corrente anche quando il subtype attuale non vi compare più
  (`IndexOf` = -1), invece di rifiutarsi quando c'è un solo candidato. Fase 8: la
  distinzione simulazione/volo è corretta; il resto è **bloccato legittimamente** in
  attesa del picker parte/asse (M3) — confermato che "bind output to first servo"
  muove davvero il servo. **Contrasto skin corretto**: `Muted` e `Faint` erano sotto
  soglia WCAG AA (~3.5:1 e ~2.5:1 contro i pannelli, letto come "grigio su grigio"),
  schiariti a ~5.3:1/~4.2:1. **Da riverificare**: la sequenza completa del ciclo
  operatore col glifo nuovo, la stabilità di posizione del bottone, e lo sblocco
  diretto (senza riaggiungere termini) di un gruppo AND/OR sceso a 1 termine.
  Prossimo dopo il retest: M3.
- **2026-07-06 (16)** — **M2 (editing) implementata e compilante** (`src/UI/KrabEditorWindow.cs`,
  `src/Graph/KrabGraphEdits.cs`, mutation API su `KrabGraph`/`KrabNode`, `ValidationIssue.nodeId`
  per l'highlighting). Bozza iniziale di Fable, **verificata e corretta** in questa sessione:
  parametri live per subtype (campi inline con `KrabUi.Field`, sanitizzazione numerica
  tollerante ai decimali con virgola), aggiungi/rimuovi termine e sotto-gruppo (con
  **pruning ricorsivo degli orfani** e compattazione dei pesi sugli spostamenti di porta),
  aggiungi/rimuovi uscita, cambio operatore del gruppo (bottone ⟳), **undo/redo** via
  snapshot testuali del grafo (stack da 50, gratis grazie al backup ConfigNode già
  esistente), **strip di validazione** in footer + evidenziazione colore dei nodi in
  errore/warning nell'albero. **Bug trovato e corretto**: `CycleOperator` sceglieva
  sempre il primo candidato in ordine d'array escludendo solo il subtype corrente, il
  che intrappolava il ciclo in un'oscillazione a due stati (WeightedSum ↔ Product) —
  Min/Max/GatedBlend/And/Or/Xor erano irraggiungibili dal bottone. Fix: rotazione vera
  a partire dalla posizione del subtype corrente nell'elenco dei compatibili per arità.
  Aggiunte 8 chiavi di localizzazione mancanti (create graph, add axis/trigger,
  add term/group, edge, no outputs). Nessun nuovo file di test temporaneo. **Da testare
  in gioco**: procedura strutturata scritta in `notes/test-editor-m2.md` (9 fasi:
  creazione da zero, costruzione albero, ciclo operatore, pruning ricorsivo alla
  rimozione, rimozione uscita, ciclo edge trigger, undo/redo, strip di validazione,
  editing sim vs volo). Priorità alle fasi 2 (fix del ciclo) e 3.1 (pruning).
  Prossimo: M3 (picker parte/campo col gesto KAL, picker sorgenti raggruppato,
  unità commutabili) — dopo il test di M2.
- **2026-07-06 (15)** — Test M1 in gioco: **funziona, nessun errore in log**; confermato
  che PlayerAxis legge solo input giocatore anche con MechJeb attivo (non l'output
  autopilota — comportamento corretto). Due fix grafici da feedback utente: maniglie
  slider dimezzate (10×16 → 5×12); **skin sostituita** da verde-KAL a **stock
  blu-grigio + accento verde acido** solo su elementi interattivi/live (palette in
  `KrabUi.cs`, riferimento in `krab-skin-mockup.html`); readout VALUE ridotto a ~2/3
  (22px → 15px). Da ritestare visivamente. Prossimo: M2 (editing).
- **2026-07-06 (14)** — **M1 editor implementata e compilante** (`src/UI/`): shell UGUI
  in codice (canvas proprio, drag da titlebar, lock input via InputLockManager on
  hover — **CTB non serve con UGUI**, decisione 4 decaduta), tab per uscita, card
  bersaglio con valore live, **albero read-only** del grafo con telemetria per nodo
  (10 Hz), **simulatore in VAB** (slider per sorgente con range per-metrica, toggle
  per ActionGroupState) che pilota lo stesso valutatore in modalità sim (override
  in EvalContext; output MAI scritti in sim). Il PAW "Open KRAB Editor" apre la
  finestra vera. Motore: compile anche in editor, `RunSimulation`, `TryGetNodeOutput`.
  Limiti M1 noti: vocabolari tecnici grezzi (loc coi picker M3), una finestra globale.
  **Da testare in gioco** (checklist in chat). Prossimo: M2 (editing).
- **2026-07-06 (13)** — Mockup layout editor approvato dall'utente (`krab-editor-mockup.html`,
  albero di gruppi). Chiuse due direzioni UI future (§9/§10 `notes/design-ui-editor.md`):
  curve in **finestra dedicata modeless** (non allargare la principale); telemetria VAB
  risolta da un **simulatore di condizioni** (uno slider per sorgente che pilota lo
  stesso valutatore — anteprima = realtà per costruzione), **da anticipare a M1/M2**
  perché senza, l'albero in VAB è cieco. Prossimo: M1 (shell UGUI + vista read-only +
  simulatore base).
- **2026-07-06 (12)** — Fase 6 (eventi/logica) **OK**: valutatore validato su tutti i
  percorsi. Decisioni UI prese (§8 `notes/design-ui-editor.md`): **albero di gruppi,
  UGUI in codice, undo, live-edit; curve custom e telemetria-in-editor rimandate**.
  Operatore **SlewRate** approvato e implementato (subtype + valutatore + catalogo);
  22 subtype totali. Prossimo: M1 dell'editor.
- **2026-07-06 (11)** — **Fase 5 chiusa: arbitraggio KAL/KRAB confermato** (media a
  priorità pari, prevalenza per priorità, con KAL in loop continuo). Percorso assi
  del valutatore completamente validato. Aggiunta la **fase 6** (eventi/logica):
  grafo campione esteso a 11 nodi col ramo "luce sotto 150 m radar con gear giù"
  (Comparator+isteresi, Not, ActionGroupState, And, 2 ActionTrigger) + evento PAW
  "bind triggers to first light" (LightOn/LightOffAction sui fronti). **Da testare:
  fase 6** (procedura in `notes/test-valutatore.md`). Scritta la bozza di design
  dell'editor UI (`notes/design-ui-editor.md`): scoperta chiave — il mockup utente
  è un **mixer strutturato ad albero di gruppi**, non un canvas a nodi; 8 punti in
  attesa di decisione utente (paradigma, stack UGUI, SlewRate, CTB, undo, ...).
- **2026-07-06 (10)** — Test valutatore fasi 1–4 + fisica: **OK** (hinge segue lo
  stick via manager, bind persiste su F5/F9, Out[0] deriva con la velocità). Fase 5:
  osservata "sovrapposizione" con KAL ≥ KRAB — quasi certamente semantica corretta
  (KAL parla solo in play, KRAB ogni frame; con loop **Once** a fine sequenza KRAB
  riprende il campo). **Da riverificare fase 5 con loop Repeat** (procedura
  aggiornata in `notes/test-valutatore.md`); strappi verso 0 durante play continuo
  = bug di timing. Aggiunte le KSPAction Toggle/Enable/Disable KRAB Controller
  (pendant di KAL) per zittire il controller dagli action group.
- **2026-07-06 (9)** — Retest B/C **passato** (grafo persiste su craft/simmetrie/sfs).
  **Valutatore implementato** (`src/Graph/Evaluation/`): 21 subtype runtime, compile
  con topo sort (rifiuta grafi con errori; nodi con parametri rotti disabilitati
  singolarmente), Run in `Update` (solo volo, gated su enabled/EC/packed), EC 0.1/s
  come KAL in `FixedUpdate`, rebind su `onVesselWasModified`. PAW: etichetta stato
  grafo + campo debug "KRAB Out[0]" + evento "bind output to first servo" (aggancia
  l'AxisOutput del grafo campione al primo servo della nave: test end-to-end con
  arbitraggio manager). **Da testare in gioco**: procedura in `notes/test-valutatore.md`
  (test interattivo: il giocatore è il generatore di segnali, niente modalità demo).
  Prossimo dopo il test: unità di visualizzazione + editor a nodi (UI).
- **2026-07-06 (8)** — Test data model: A (prefab) OK, D (self-test) PASS; B/C falliti —
  craft senza KRAB_GRAPH. Causa capita e corretta: **le istanze editor sono cloni Unity
  del prefab, OnLoad non gira sul clone** e il ConfigNode privato non sopravvive.
  Fix: backup del grafo in `[SerializeField] string` (ConfigNode.ToString/Parse),
  ripristino in OnStart/OnSave. Lezione generalizzata in `notes/schema-serializzazione.md`
  (ogni dato complesso del modulo deve avere forma Unity-serializzabile).
  **Da ritestare in gioco: punti B e C** (craft con KRAB_GRAPH + futureParam,
  quicksave/load). Prossimo dopo il retest: valutatore del grafo.
- **2026-07-06 (7)** — **Data model del grafo implementato** (`src/Graph/`): parser e
  writer di KRAB_GRAPH con ritenzione dei ConfigNode originali (round-trip completo,
  subtype sconosciuti conservati e disabilitati), registro subtype dal catalogo,
  validazione completa (id, porte con arità fissa/dinamica, DEFAULT, cicli con
  percorso, output attivi, orfani). Integrato nel modulo (parse+log in OnLoad, save
  dal grafo con fallback al nodo raw). Self-test in codice richiamabile dal PAW con
  **Advanced Tweakables attivi** ("KRAB: run graph self-test" → PASS/FAIL a schermo,
  dettagli in KSP.log). `graphVersion`/`nextNodeId` spostati dentro KRAB_GRAPH.
  **Da testare in gioco**: self-test PASS. Prossimo: valutatore del grafo (sorgenti
  + operatori + output via RoboticManagerBridge).
- **2026-07-06 (6)** — **Axis promoter implementato, testato in gioco: OK** (promozione
  RCS/ModuleRCSFX + reaction wheel confermata, campi selezionabili anche in KAL).
- **2026-07-06 (5)** — **Primo test in gioco superato**: assembly ok, PAW ok, i 4 slot
  compaiono negli Axis Group con i 5 set. Modello: il nome giusto dell'asset DLC è
  `.../Controllers/controller1000` (corretto nel cfg). Due feature aggiunte al design
  su richiesta utente: **porte logiche** (And/Or/Not/Xor + sorgente ActionGroupState,
  convenzione booleana ≥ 0.5) e **promozione di KSPField a KSPAxisField** (RCS
  thrustPercentage, reaction wheel authorityLimiter → assegnabili agli Axis Group;
  fattibilità verificata, design in `notes/promozione-axis-field.md`; porta la
  dipendenza da Harmony 2). Prossimo: data model del grafo (parser + validazione DAG).
- **2026-07-06 (4)** — **Progetto C# impostato e compilante**: skeleton di
  `ModuleKRABController` (campi persistenti, 4 slot ControllerInput come KSPAxisField
  assoluti, round-trip di KRAB_GRAPH, guard DLC, stringhe tutte localizzate),
  `RoboticManagerBridge` (delegate cacheato su `QueueFieldUpdate` con fallback a
  `SetValue`), `Localization/en-us.cfg`, parte `Parts/krab9000.cfg` (modello KAL
  placeholder — **da verificare in gioco** che il MODEL{} risolva l'asset del DLC).
  Prossimi passi: test di caricamento in gioco → poi parsing/valutazione del grafo.
- **2026-07-06 (3)** — Catalogo nodi rivisto e **chiuso** con l'utente: sorgenti fisiche
  in unità umane SI (commutazione unità solo in UI), 4 slot ControllerInput, Hold
  confermato, **più AxisOutput per istanza** (vincolo rilassato; UX bersagli = gesto di
  KAL), minInterval dell'ActionTrigger interno e non esposto in UI. **Design completo:
  si può impostare il progetto C#.** Nessun codice ancora scritto.
- **2026-07-06 (2)** — Discussione di design: KRAB = **mixer puro** (no timeline), pesi
  per-porta, eventi via **ActionTrigger**, PlayerAxis/ScriptAxis subtype distinti,
  round-trip completo, modulo `ModuleKRABController`. Scritto il catalogo nodi.
- **2026-07-06 (1)** — Chiusi i 3 punti preliminari: analisi KAL sul decompilato, schema
  di serializzazione (Opzione A), censimento compatibilità.

## Layout della cartella — DOPPIA RADICE dal 2026-07-25

**Attenzione, non è un errore**: da questa data `GameData/KRAB/` (la cartella
di questo hub) contiene **due livelli** con ruoli diversi, voluti dall'utente
per rispecchiare la struttura del repo GitHub anche qui in locale:

- **`GameData/KRAB/` (radice, questo livello)** = **radice del repo**. Sorgenti,
  documentazione, note interne: tutto ciò che è "il progetto", non "la mod
  installata". È quello che `git` traccia (a parte `notes/`/`CLAUDE.md`, esclusi
  via `.gitignore`).
- **`GameData/KRAB/GameData/KRAB/`** = la **mod vera e propria**, quella che va
  in un install reale di KSP e quella che finisce nel package di release. Si
  chiama così perché rispecchia esattamente `GameData/<NomeMod>/` dentro il
  repo GitHub. Siccome questa sotto-cartella si trova comunque dentro
  l'installazione KSP di sviluppo (che scansiona `GameData/` ricorsivamente,
  senza limite di profondità), il gioco la carica esattamente come farebbe
  con un install reale — **nessun passaggio di copia separato per testare**.

Quando si parla di "Config/", "Localization/", "Parts/", "Plugins/",
"Textures/", "README.md", "CHANGELOG.md" altrove in questo file, si intende
sempre il percorso dentro `GameData/KRAB/GameData/KRAB/...`, non la radice.

```
GameData/KRAB/                      ← RADICE REPO (mirror del layout GitHub)
├── CLAUDE.md                    ← questo file (hub, stato, convenzioni) — gitignored
├── KRAB-9000_design_doc.md      ← design doc (fonte dei requisiti)
├── LICENSE                      ← MIT
├── .gitignore
├── docs/                        ← branding (loghi) + manuale utente per la wiki GitHub
│   ├── KRAB_logo.png / KRAB_logo2.png
│   └── wiki/User-Guide.md
├── Test/GraphSample.cfg.txt     ← DISATTIVATO, gitignored (vedi registro sotto)
├── notes/                       ← appunti tecnici — gitignored, SOLO locali
│   ├── KAL-1000-analysis.md         ← come funziona davvero KAL (verificato sul codice)
│   ├── schema-serializzazione.md    ← schema ConfigNode del grafo (Opzione A)
│   ├── catalogo-nodi.md             ← nodi e parametri per subtype (CHIUSO)
│   ├── compatibilita-modset.md      ← interazioni con le altre mod installate
│   ├── test-valutatore.md           ← procedura test in gioco del valutatore
│   ├── test-editor-m2.md            ← procedura test in gioco dell'editing (M2)
│   ├── test-editor-m3.md            ← procedura test in gioco picker/unità/segnali (M3)
│   ├── test-bug-3.5-4.2.md          ← indagine e fix dei due bug (RISOLTI)
│   ├── test-fase2-esteso.md         ← protocollo fase 2 (test esteso, un solo craft, sez. A-H)
│   ├── test-editor-m4.md            ← procedura test in gioco finestra curva (M4)
│   ├── design-ui-editor.md          ← design editor UI (M1-M4 tutte implementate)
│   ├── icon-spec.md                 ← specifica icone custom (IMPLEMENTATA)
│   ├── prossimi-passi.md            ← piano test esteso (fase 2) + M4 (fase 3, implementata)
│   └── archive/                     ← mockup superati (riferimento storico)
│       ├── kab-1000-mixer-mockup.html
│       ├── krab-editor-mockup.html
│       └── krab-skin-mockup.html
├── src/                         ← sorgenti C# (KSP li ignora)
│   ├── KRAB9000.csproj              ← net472, refs a KSP_x64_Data/Managed + 0Harmony;
│   │                                   copia la DLL in ../GameData/KRAB/Plugins/ (path aggiornato 2026-07-25)
│   ├── Directory.Build.props        ← obj/bin su C: (%LOCALAPPDATA%\KRAB-9000)
│   ├── ModuleKRABController.cs      ← PartModule principale (debugMode = gate cfg-only)
│   ├── AxisPromoter.cs              ← promozione KSPField→BaseAxisField (Harmony)
│   ├── RoboticManagerBridge.cs      ← delegate cacheato su QueueFieldUpdate (internal)
│   ├── UI/                          ← editor UI (UGUI in codice)
│   │   ├── KrabUi.cs                    ← factory + palette + icone custom + tooltip
│   │   ├── KrabUnits.cs                 ← unità di visualizzazione (conversione solo-UI)
│   │   ├── KrabEditorWindow.cs          ← finestra: albero, undo, simulatore, picker, evidenziazione bersaglio
│   │   ├── KrabCurveLine.cs             ← M4: polyline UGUI (MaskableGraphic) per l'anteprima curva
│   │   └── KrabCurveWindow.cs           ← M4: finestra dedicata curva di risposta di Remap
│   └── Graph/                       ← data model del grafo
│       ├── KrabGraph.cs                 ← container, Load/Save, Validate, mutation API
│       ├── KrabNode.cs                  ← nodo con ConfigNode ritenuto; Create/SetParam/SetSubtype
│       ├── KrabGraphElements.cs         ← KrabLink, KrabPortDefault, KrabNodeUi
│       ├── KrabGraphEdits.cs            ← operazioni composite (add/remove/cycle/copy/paste) per l'editor
│       ├── KrabSubtypes.cs              ← registro subtype (parse tollerante)
│       ├── ValidationIssue.cs           ← esito validazione, ora localizzato (LocalizedText)
│       ├── KrabGraphSelfTest.cs         ← self-test in-game (via PAW, gated da debugMode)
│       └── Evaluation/                  ← valutatore runtime (quello VERO, in uso)
│           ├── KrabEvaluator.cs             ← compile (topo sort, resiliente agli errori) + Run per frame
│           ├── RuntimeNode.cs               ← base, contesto, binding porte
│           ├── RuntimeSources.cs            ← 6 sorgenti (canali, fisica, AG state)
│           ├── RuntimeOperators.cs          ← 13 operatori (stateful inclusi)
│           └── RuntimeOutputs.cs            ← AxisOutput (via manager) + ActionTrigger
└── GameData/KRAB/               ← ★ LA MOD INSTALLABILE (questo va in un GameData/ reale)
    ├── CHANGELOG.md                  ← voce [0.1.0]
    ├── README.md
    ├── Config/AxisPromotions.cfg     ← regole dell'axis promoter (RCS + reaction wheel)
    ├── Localization/
    │   ├── en-us.cfg                     ← master inglese, #LOC_KRAB_*, commento per riga
    │   └── it-it.cfg                     ← italiano, parità completa con en-us
    ├── Parts/krab9000.cfg            ← parte KRAB-9000 (modello KAL, retexture custom)
    ├── Plugins/KRAB9000.dll          ← output di build (destinazione aggiornata 2026-07-25)
    └── Textures/                     ← icone custom UI (icon_*.png) + texture parte
```

**Build**: `dotnet build` dentro `src/` — la DLL viene copiata in
`../GameData/KRAB/Plugins/` automaticamente (path aggiornato dopo la
riorganizzazione a doppia radice del 2026-07-25); gli intermedi stanno in
`%LOCALAPPDATA%\KRAB-9000` (regola exFAT).

Regola dal design doc (interpretazione corretta dall'utente, 2026-07-06): i file vanno
**organizzati per ruolo** — `.cs` in `src/`, `.md` in `notes/`, i file effettivi della
mod nelle cartelle che andranno a release, i cfg di test in `Test/`. I file di test
(anche `.cfg`) **sono ammessi** quando validano linguaggi e logiche che saranno quelle
della mod completa: la cartella deve restare compatibile col testing in gioco. Il punto
critico per i file temporanei/placeholder non è evitarli ma **tenerne traccia** e poi
aggiornarli, pulirli o rimuoverli: ogni file o elemento temporaneo va registrato qui
sotto.

## Registro file temporanei / placeholder (aggiornare SEMPRE)

| Elemento | Stato | Da fare prima della release |
|---|---|---|
| `Parts/krab9000.cfg` | placeholder | modello proprio (ora riusa il KAL); EC 0.1/s aggiunto il 2026-07-06 |
| `Test/GraphSample.cfg.txt` | **disattivato** (2026-07-17) | patch MM che iniettava un KRAB_GRAPH campione — non più necessaria ora che la fase 2 (test esteso) è chiusa e M2/M3/M4 sono stati verificati anche costruendo il grafo a mano dall'editor; rinominato `.txt` (ModuleManager non lo legge più) invece di eliminarlo, coerente con la regola "niente git = nessuna rete di sicurezza per un'eliminazione" |
| Evento PAW "run graph self-test" + eventi "bind output/trigger" + campo `debugOutputValue` | **debug permanente, non temporaneo** | decisione utente 2026-07-09: **restano nel codice** come rete di sicurezza per regressioni, ma nascosti di default — visibili solo con `debugMode = true` nel MODULE del cfg della parte (mai un toggle PAW/Advanced Tweakables) |
| `src/Eval/*.cs.disable` (ex `KrabEvaluator.cs`, `NodeEvaluators.cs`, `OutputBindings.cs`) | **disattivato, tenuto così** (decisione utente 2026-07-09) | nessun repository git in questa cartella → nessuna rete di sicurezza per un'eliminazione; restano `.disable` finché non si è certi di non doverli più consultare |
| `notes/archive/*.html` (3 mockup) | **archiviati** (2026-07-09) | riferimento storico delle decisioni di design; non toccare |

## Codice sorgente KSP decompilato

- File chiave in chiaro: `<radice KSP>/Claude/ksp-decomp-key/` (Expansions.Serenity
  completo + BaseAxisField, AxisGroupsModule, Vessel, FlightInputHandler, FlightCtrlState,
  FloatCurve, ecc.)
- Sorgente completo (3300 file): `<radice KSP>/Claude/ksp-decomp-full.zip` — estrarre
  **nella scratchpad**, non su E: (exFAT, cluster 1 MB → 35 MB diventano 3.3 GB).
- Rigenerazione: `dotnet tool install ilspycmd --tool-path <dir>` poi
  `ilspycmd -p -o <outdir> "KSP_x64_Data/Managed/Assembly-CSharp.dll"`.
- Il decompilato contiene junk anti-decompilazione (`while(true) switch(...)`): ignorarlo.

## Decisioni architetturali prese (dettagli nelle note)

1. KRAB scrive i valori via `RoboticControllerManager.QueueFieldUpdate` (stessa via di
   KAL): priorità e coesistenza con i KAL esistenti risolte dal gioco.
2. Gli ingressi PlayerAxis di KRAB sono `KSPAxisField`: i 5 set di Action Groups
   funzionano da soli.
3. Serializzazione grafo: lista piatta NODE/LINK/DEFAULT/UI con id stabili e parse
   tollerante (Opzione A).
4. Due subtype distinti: PlayerAxis = stato pre-catena (input giocatore puro),
   ScriptAxis = `vessel.ctrlState` post-`FeedInputFeed`; mai scrivere sugli hook.
5. Dipendenza dura da Breaking Ground, con guard di autodistruzione come KAL.
6. KRAB è un **mixer puro**: niente timeline interna, sequenze via nesting con KAL.
7. Eventi discreti: nodo **ActionTrigger** (fronte salita/discesa/entrambi).
8. Pesi del Weighted Sum per-porta (`weights`); LINK = puro cablaggio.
9. Round-trip completo dei ConfigNode (value sconosciuti preservati);
   modulo `ModuleKRABController`.
10. Sorgenti fisiche in **unità umane SI** (m/s, m, kPa...); commutazione di unità
    coerenti solo in visualizzazione, valore canonico invariante.
11. **Più AxisOutput per istanza**; ingressi nel menù AG (4 slot ControllerInput),
    bersagli nella UI di KRAB con lo stesso gesto di KAL. Semplicità d'uso prioritaria.
12. **Axis promoter** (richiesta esplicita utente): promozione runtime a KSPAxisField
    di `ModuleRCS.thrustPercentage` (copre ModuleRCSFX) **e** di
    `ModuleReactionWheel.authorityLimiter` — entrambi obbligatori nello scope.
    Config-driven (`Config/AxisPromotions.cfg`, MM-patchable), postfix Harmony su
    `AxisGroupsManager.BuildBaseAxisFields`. Design: `notes/promozione-axis-field.md`.
13. **Porte logiche** And/Or/Not/Xor + sorgente ActionGroupState; convenzione
    booleana ≥ 0.5 = vero in tutto il grafo.

## Vincoli da non dimenticare

- Gli eventi di KAL non scattano in modalità asse e non sono direzionali: KRAB deve avere
  un proprio percorso eventi (Derivative + Comparator).
- Valutazione/applicazione in `Update` (frame video), non `FixedUpdate`.
- Non toccare mai i transform dei servo (KSPCommunityFixes RoboticsDrift).
- **Istanze editor = cloni Unity, OnLoad non gira sul clone**: ogni dato complesso dei
  PartModule deve avere una forma Unity-serializzabile ([SerializeField]); il grafo
  usa un backup stringa da tenere aggiornato a ogni mutazione (vedi schema note).
- Ambiente: KSP 1.12.5, entrambi i DLC, ModuleManager 4.2.3.
- Dipendenze della mod: Breaking Ground (dura) + **Harmony 2** (per l'axis promoter).
- Modelli DLC via MODEL{}: l'URL usa il **nome della parte** (es.
  `SquadExpansion/Serenity/Parts/Robotics/Controllers/controller1000`), non `model`.
