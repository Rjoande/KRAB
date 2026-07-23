# KRAB-9000 (Kerbal Routing & Axis Blender) — Documento di contesto per Code

## Obiettivo del progetto

Mod per Kerbal Space Program che introduce una nuova parte, **KRAB-9000**, la quale estende le capacità del controller nativo **KAL-1000** senza raggiungere la complessità di kOS. KRAB deve rimanere un editor visuale a nodi, non uno scripting testuale.

KAL-1000 è, nel codice reale, un *sequencer temporale*: lo stato primario è una posizione in secondi lungo la sequenza, con play/loop/direzione; la posizione può essere pilotata da un axis group (13 disponibili: Pitch/Yaw/Roll, TranslateX/Y/Z, MainThrottle, WheelSteer/WheelThrottle, Custom01-04) perché è essa stessa un campo asse assegnabile. Le curve di risposta sono per-asse-controllato, con eventi singoli lungo la sequenza. Analisi completa verificata sul decompilato: `notes/KAL-1000-analysis.md`. I limiti noti di KAL che KRAB deve superare:

- non può usare come input stati fisici della nave (velocità, altitudine, pressione, G-force, ecc.);
- non può combinare più input in un'unica curva;
- gli eventi singoli non sono sensibili alla direzione di percorrenza della curva;
- gli eventi non scattano affatto quando la posizione è pilotata da un asse anziché dal play della sequenza (verificato: `CheckAndFireActions` gira solo in play);
- legge solo input utente, non output di script/autopiloti (SAS, MechJeb, AtmosphereAutopilot).

## Principio architetturale centrale

**L'output di KRAB deve essere indistinguibile, per la parte bersaglio, da quello prodotto da KAL.** KRAB controlla le parti esattamente come farebbe KAL: cambia solo il modo in cui il valore viene costruito a monte (combinazione di più input tramite un grafo di nodi/operatori).

*Precisazione verificata sul codice (2026-07-06)*: la parte bersaglio non consuma mai una curva. KAL valuta la propria FloatCurve ogni frame e accoda un valore scalare via `RoboticControllerManager.QueueFieldUpdate(BaseAxisField, valore, priorità)`; il manager applica per ogni campo il valore a priorità più alta (media a pari priorità). KRAB deve quindi spingere i propri valori per-frame nella stessa coda: questo garantisce indistinguibilità per la parte e risolve gratis l'arbitraggio con eventuali KAL concorrenti.

## Requisiti funzionali principali

- UI dedicata accessibile da PAW della parte, sia in editor che in volo (come KAL).
- KRAB selezionabile come input per ciascuna parte nel menù Action Groups.
- Comportamento rispetto ai 5 set di Action Groups (1 default + 4 custom di Breaking Ground) allineato a quello di KAL — da verificare nel codice reale di KAL, non assunto.
- Editor a nodi: gli input sono selezionati direttamente nell'editor di KRAB, raggruppati per tipo di operazione (l'equivalente visuale delle parentesi in un'espressione, senza che il giocatore debba scrivere testo).
- Filtro/smoothing configurabile sulle sorgenti di stato fisico, con rate di campionamento non inferiore a 1/10 s (default), da validare/calibrare in fase di test.
- **Promozione di campi stock a KSPAxisField** (aggiunta 2026-07-06): il limitatore di spinta RCS (`ModuleRCS.thrustPercentage`) e l'autorità delle reaction wheel (`ModuleReactionWheel.authorityLimiter`) devono diventare assegnabili agli Axis Group (e quindi bersagli validi per KAL/KRAB). Fattibilità verificata sul codice; design in `notes/promozione-axis-field.md`. Comporta dipendenza da Harmony 2.
- Script input (SAS/MechJeb/AtmosphereAutopilot) trattati come sorgente distinta da input utente puro; punto di aggancio (pre o post autopilota) da decidere in base a dove il valore è effettivamente leggibile nel codice di KSP, secondo preferenza del giocatore.

## Schema dati (bozza di partenza, non vincolante)

- **KRAB_NODE**: `id`, `type` (SOURCE / OPERATOR / OUTPUT), `subtype`, parametri specifici.
- **KRAB_PORT**: ingresso/uscita di un nodo. SOURCE ha solo uscita; OPERATOR ha N ingressi (variabili per Weighted Sum, fissi a 2 per Min/Max) e 1 uscita.
- **KRAB_LINK**: `fromNodeId`, `toNodeId`, `toPortIndex`, `weight` opzionale.
- **KRAB_SOURCE** (sottotipo SOURCE): `PlayerAxis`, `ScriptAxis`, `PhysicalState` (con parametro di sampling rate e filtro).
- **KRAB_OUTPUT**: nodo terminale unico per istanza, produce la curva finale nel formato consumato oggi da KAL.

Vincoli di validazione: grafo aciclico (DAG), almeno un nodo terminale raggiungibile (il vincolo originario "un solo KRAB_OUTPUT" è stato rilassato il 2026-07-06: più output per istanza, con UX di aggiunta bersagli identica al gesto di KAL), ogni porta OPERATOR deve avere un input connesso o un default esplicito.

Lo schema di serializzazione su ConfigNode (rappresentazione del grafo, ID nodi/porte, layout UI) **va deciso insieme a Code**, con alternative e trade-off, non è ancora fissato.

## Set di operatori (razionalizzato)

Set minimo e componibile, pensato per coprire i casi d'uso sotto senza proliferazione di nodi ad hoc:

1. **Weighted Sum** — N input, peso per input, clamp/remap opzionale in uscita. Copre anche sottrazione (peso negativo) e logiche di tipo "errore = target − valore attuale".
2. **Product** — moltiplica due o più input normalizzati.
3. **Min / Max** — seleziona il valore più basso/alto tra due input (limitatori, override).
4. **Clamp / Remap** — restringe/riscala un input o un risultato intermedio su un sotto-range.
5. **Gated Blend / Crossfade con isteresi** — interpola tra due sorgenti in base a un terzo input di controllo che attraversa una soglia, con zona di transizione configurabile.
6. **Rate of Change / Derivative** — segno/intensità della variazione di un input nel tempo.
7. **Comparator / Threshold Gate** — uscita 0/1 quando un input attraversa un valore soglia.
8. **Sample-and-Hold / Latch** — congela un valore su trigger, fino a condizione di reset.
9. **Porte logiche (And / Or / Not / Xor)** — combinano segnali booleani (convenzione: ≥ 0.5 = vero) per condizioni di input e azioni evento/toggle. Aggiunte il 2026-07-06; sorgente correlata: ActionGroupState (stato on/off di un action group). Caso d'uso: luce accesa solo se quota radar < 150 m E "dispositivi di atterraggio" ON.

La sensibilità di direzione degli eventi (limite noto di KAL) si ottiene componendo Derivative + Comparator, senza bisogno di un operatore dedicato.

### Casi d'uso di riferimento (validazione del set di operatori)

1. Custom1 posiziona un servo 0–90°; il beccheggio (fisico o script) lo fa oscillare di ±5° attorno al punto impostato → Weighted Sum + Clamp.
2. Sotto una soglia di velocità orizzontale combinata a posizione di un servo, un attuatore resta fisso; sopra soglia recupera gradualmente l'authority nativa, con fase ibrida → Gated Blend con isteresi.
3. Evento "accensione postbruciatore" solo su curva throttle in salita, "spegnimento" solo in discesa → Derivative + Comparator.

Riscontri dai forum ufficiali KSP che confermano la rilevanza di questi casi (nessuna fonte Reddit pertinente trovata):
- Richiesta di un asse che torni a un valore di riposo al rilascio di un comando diverso da quello che lo ha spostato, con relativi conflitti di priorità tra più KAL sulla stessa parte (forum.kerbalspaceprogram.com/topic/190912) — coperto da Sample-and-Hold + Weighted Sum.
- Richiesta di usare strumenti di volo (es. IAS da tubo di Pitot) come input KAL per logiche di tipo proporzionale (es. airbrake che si dispiega per mantenere la velocità) (forum.kerbalspaceprogram.com/topic/199041) — coperto da PhysicalState + Weighted Sum.

## Contesto ambiente di lavoro

- Il giocatore lavorerà su una copia della cartella Kerbal Space Program contenente l'intero modset installato, così Code potrà valutare fin da subito eventuali problemi/opportunità di compatibilità con altre mod presenti.
- Code avrà accesso al codice sorgente/decompilato di KSP: va usato per verificare il reale funzionamento di KAL-1000 (formato curva, gestione Action Groups/set, PartModule coinvolti) invece di assumerlo dalla documentazione.
- Workflow previsto: `CLAUDE.md` come layer di collegamento tra sessioni di pianificazione in chat e sessioni operative in Claude Code. Tutti i file di lavoro, incluso CLAUDE.md, dovranno stare in GameData/KRAB, mantenendo sempre ordine tra file temporanei, appunti, bozze, e quello che sarà il mod vero e proprio. La cartella KRAB e il suo contenuto devono essere compatibili per testing in gioco.

## Stato dei punti aperti (aggiornato 2026-07-06)

- ~~Formato di serializzazione del grafo in ConfigNode~~ → **Opzione A approvata in linea di massima** (lista piatta NODE/LINK/DEFAULT/UI con id stabili e parse tollerante); dettagli residui in `notes/schema-serializzazione.md`.
- Punto di aggancio esatto per ScriptAxis: entrambe le letture sono disponibili e verificate (`Vessel.FeedInputFeed`: input giocatore puro pre-catena, output script post-catena su `vessel.ctrlState`) → resta da decidere solo l'esposizione all'utente (per sorgente o globale). Vincolo: solo lettura, mai scrivere sugli hook.
- ~~Comportamento rispetto ai 5 set di Action Groups~~ → **chiuso**: i 5 set vivono in `BaseAxisField` (`axisGroup` + `overrideGroups[0..3]`) e `Vessel.GroupOverride`; esponendo gli ingressi di KRAB come `KSPAxisField` il comportamento è ereditato senza codice dedicato.
- ~~Conflitti di priorità tra più KRAB/KAL sulla stessa parte~~ → **chiuso**: già arbitrati da `RoboticControllerManager` (priorità 1-5, alta vince, pari = media) purché KRAB passi da `QueueFieldUpdate`.

Decisioni di design chiuse il 2026-07-06 (discussione con Code):

- **KRAB è un mixer puro**: grafo valutato ogni frame, nessuna timeline interna; le
  sequenze temporali si ottengono pilotando un KAL via nesting (nativo, verificato).
- **Peso per-porta nel Weighted Sum** (`weights`); i LINK sono puro cablaggio.
- **Eventi via nodo ActionTrigger** (fronte salita/discesa/entrambi sull'ingresso,
  tipicamente da Comparator/Derivative): risolve direzionalità e funzionamento fuori dal
  play, i due limiti di KAL.
- **PlayerAxis (pre-autopilota) e ScriptAxis (post-autopilota) come subtype distinti**;
  il contributo "solo script" si compone nel grafo (ScriptAxis − PlayerAxis).
- Round-trip completo dei value sconosciuti nei ConfigNode; modulo `ModuleKRABController`.
- **Sorgenti fisiche in unità umane** (m/s, m, kPa, m/s²...), commutazione di unità
  coerenti solo in visualizzazione; assi di comando nominali −1..+1.
- **Più AxisOutput per istanza** (vincolo "uno solo" rilassato); ingressi esposti nel
  menù Action Groups (4 slot ControllerInput), bersagli gestiti nella UI di KRAB con
  lo stesso gesto di KAL. Priorità assoluta alla semplicità d'uso (anti-kOS).

Catalogo completo di nodi e parametri: `notes/catalogo-nodi.md` — **rivisto e chiuso
il 2026-07-06**. Il design è pronto per l'implementazione.
