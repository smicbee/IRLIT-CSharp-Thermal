#include <Arduino.h>
#include <vector>

static const uint32_t BAUD = 115200;

const char *HELP_COMMANDS =
  "Available commands:\n"
  "HELLO\n"
  "PINS\n"
  "SET,<pin>,<HIGH|LOW|1|0>\n"
  "TOGGLE,<pin>\n"
  "READ,<pin>\n"
  "STOP,<pin>\n"
  "PULSE,<pin>,<HIGH|LOW>,<ms1>,<ms2>,...[ ,LOOP]\n"
  "PULSEUS,<pin>,<HIGH|LOW>,<us1>,<us2>,...[ ,LOOP]";

// Passe an: nur diese Pins werden akzeptiert
const int allowedPins[] = {0, 1, 2, 22, 23, 16, 18, 20, 19, 17};
constexpr size_t allowedPinsCount = sizeof(allowedPins) / sizeof(allowedPins[0]);

bool isAllowedPin(int pin) {
  for (int p : allowedPins) if (p == pin) return true;
  return false;
}

String rxLine;

String nextToken(String &s) {
  s.trim();
  int idx = s.indexOf(',');
  if (idx < 0) {
    String t = s;
    s = "";
    t.trim();
    return t;
  }
  String t = s.substring(0, idx);
  s = s.substring(idx + 1);
  t.trim();
  return t;
}

void replyOK(const String &msg)  { Serial.println("OK," + msg); }
void replyERR(const String &msg) { Serial.println("ERR," + msg); }

void replyPins() {
  // Ausgabeformat: OK,PINS,0,1,2,22,...
  String out = "PINS";
  for (size_t i = 0; i < allowedPinsCount; i++) {
    out += ",";
    out += String(allowedPins[i]);
  }
  replyOK(out);
}

struct PulseJob {
  bool active = false;
  int pin = -1;
  bool level = false;
  std::vector<uint32_t> durs;   // durations (ms oder µs je nach useMicros)
  size_t idx = 0;
  uint32_t nextSwitchAt = 0;    // millis()/micros()
  bool useMicros = false;       // ms oder µs
  bool loop = false;            // Pulsfolge loopen
};

PulseJob job;

bool parseLevel(const String &sIn, bool &outLevel) {
  String s = sIn; s.trim(); s.toUpperCase();
  if (s == "HIGH" || s == "1") { outLevel = true; return true; }
  if (s == "LOW"  || s == "0") { outLevel = false; return true; }
  return false;
}

void stopPulseIfPin(int pin) {
  if (job.active && job.pin == pin) {
    job.active = false;
    job.loop = false;
    replyOK("STOP," + String(pin));
  }
}

void startPulse(int pin, bool startLevel,
                const std::vector<uint32_t> &durations,
                bool useMicros) {

  job.active = false;
  job.pin = pin;
  job.level = startLevel;
  job.durs = durations;
  job.idx = 0;
  job.useMicros = useMicros;
  job.loop = false; // Default: kein Loop (kann beim Command gesetzt werden)

  pinMode(pin, OUTPUT);
  digitalWrite(pin, job.level ? HIGH : LOW);

  uint32_t now = useMicros ? micros() : millis();
  job.nextSwitchAt = now + job.durs[0];
  job.active = true;

  replyOK(String("PULSE,START,") + pin + "," + (useMicros ? "US" : "MS"));
}

void updatePulse() {
  if (!job.active) return;

  uint32_t now = job.useMicros ? micros() : millis();

  if ((int32_t)(now - job.nextSwitchAt) >= 0) {

    // Ende der Liste erreicht?
    if (job.idx + 1 >= job.durs.size()) {
      if (job.loop) {
        // Loop: wieder von vorne anfangen
        job.idx = 0;
        job.level = !job.level;
        digitalWrite(job.pin, job.level ? HIGH : LOW);
        job.nextSwitchAt += job.durs[0];
        return;
      } else {
        job.active = false;
        replyOK("PULSE,DONE," + String(job.pin));
        return;
      }
    }

    // normaler Schritt
    job.level = !job.level;
    digitalWrite(job.pin, job.level ? HIGH : LOW);

    job.idx++;
    job.nextSwitchAt += job.durs[job.idx];
  }
}

// ---------- Command Handler ----------
void handleLine(const String &line) {
  String s = line;
  s.trim();
  if (s.length() == 0) return;

  String cmd = nextToken(s);
  cmd.toUpperCase();

  if (cmd == "HELLO") {
    replyOK("GPIOController v1.0");
    return;
  }

  if (cmd == "PINS") {
    replyPins();
    return;
  }

  if (cmd == "SET") {
    String pinStr = nextToken(s);
    String valStr = nextToken(s);
    valStr.toUpperCase();

    if (pinStr.length() == 0 || valStr.length() == 0) {
      replyERR("SET expects: SET,<pin>,<HIGH|LOW|1|0>");
      return;
    }

    int pin = pinStr.toInt();
    if (!isAllowedPin(pin)) { replyERR("pin not allowed"); return; }

    bool high;
    if (!parseLevel(valStr, high)) { replyERR("bad level"); return; }

    stopPulseIfPin(pin); // wenn puls läuft, abbrechen

    pinMode(pin, OUTPUT);
    digitalWrite(pin, high ? HIGH : LOW);
    replyOK("SET," + String(pin) + "," + (high ? "HIGH" : "LOW"));
    return;
  }

  if (cmd == "TOGGLE") {
    String pinStr = nextToken(s);
    if (pinStr.length() == 0) { replyERR("TOGGLE expects: TOGGLE,<pin>"); return; }
    int pin = pinStr.toInt();
    if (!isAllowedPin(pin)) { replyERR("pin not allowed"); return; }

    stopPulseIfPin(pin);

    pinMode(pin, OUTPUT);
    int cur = digitalRead(pin);
    int next = (cur == HIGH) ? LOW : HIGH;
    digitalWrite(pin, next);
    replyOK("TOGGLE," + String(pin) + "," + (next == HIGH ? "HIGH" : "LOW"));
    return;
  }

  if (cmd == "READ") {
    String pinStr = nextToken(s);
    if (pinStr.length() == 0) { replyERR("READ expects: READ,<pin>"); return; }
    int pin = pinStr.toInt();
    if (!isAllowedPin(pin)) { replyERR("pin not allowed"); return; }

    pinMode(pin, INPUT);
    int val = digitalRead(pin);
    replyOK("READ," + String(pin) + "," + (val == HIGH ? "HIGH" : "LOW"));
    return;
  }

  if (cmd == "STOP") {
    String pinStr = nextToken(s);
    if (pinStr.length() == 0) { replyERR("STOP expects: STOP,<pin>"); return; }
    int pin = pinStr.toInt();
    if (!isAllowedPin(pin)) { replyERR("pin not allowed"); return; }
    stopPulseIfPin(pin);
    return;
  }

  // PULSE in ms: PULSE,<pin>,<startLevel>,<ms...>[,LOOP]
  if (cmd == "PULSE") {
    String pinStr = nextToken(s);
    String startStr = nextToken(s);

    if (pinStr.length() == 0 || startStr.length() == 0) {
      replyERR("PULSE expects: PULSE,<pin>,<HIGH|LOW|1|0>,<ms1>,<ms2>,...[ ,LOOP]");
      return;
    }

    int pin = pinStr.toInt();
    if (!isAllowedPin(pin)) { replyERR("pin not allowed"); return; }

    bool startLevel;
    if (!parseLevel(startStr, startLevel)) { replyERR("bad start level"); return; }

    std::vector<uint32_t> durs;
    bool loopFlag = false;

    while (s.length() > 0) {
      String t = nextToken(s);
      if (t.length() == 0) break;

      String tUp = t; tUp.trim(); tUp.toUpperCase();
      if (tUp == "LOOP") { loopFlag = true; break; } // LOOP nur am Ende

      long v = t.toInt();
      if (v <= 0) { replyERR("duration must be >0 ms"); return; }
      durs.push_back((uint32_t)v);
    }

    if (durs.empty()) { replyERR("need at least one duration"); return; }

    stopPulseIfPin(pin);
    startPulse(pin, startLevel, durs, false);
    job.loop = loopFlag;
    if (loopFlag) replyOK("PULSE,LOOP,ON," + String(pin));
    return;
  }

  // PULSEUS in µs: PULSEUS,<pin>,<startLevel>,<us...>[,LOOP]
  if (cmd == "PULSEUS") {
    String pinStr = nextToken(s);
    String startStr = nextToken(s);

    if (pinStr.length() == 0 || startStr.length() == 0) {
      replyERR("PULSEUS expects: PULSEUS,<pin>,<HIGH|LOW|1|0>,<us1>,<us2>,...[ ,LOOP]");
      return;
    }

    int pin = pinStr.toInt();
    if (!isAllowedPin(pin)) { replyERR("pin not allowed"); return; }

    bool startLevel;
    if (!parseLevel(startStr, startLevel)) { replyERR("bad start level"); return; }

    std::vector<uint32_t> durs;
    bool loopFlag = false;

    while (s.length() > 0) {
      String t = nextToken(s);
      if (t.length() == 0) break;

      String tUp = t; tUp.trim(); tUp.toUpperCase();
      if (tUp == "LOOP") { loopFlag = true; break; } // LOOP nur am Ende

      long v = t.toInt();
      if (v <= 0) { replyERR("duration must be >0 us"); return; }
      durs.push_back((uint32_t)v);
    }

    if (durs.empty()) { replyERR("need at least one duration"); return; }

    stopPulseIfPin(pin);
    startPulse(pin, startLevel, durs, true);
    job.loop = loopFlag;
    if (loopFlag) replyOK("PULSEUS,LOOP,ON," + String(pin));
    return;
  }

  replyERR(String("unknown command\n") + HELP_COMMANDS);
}

void setup() {
  Serial.begin(BAUD);
  delay(200);
  Serial.println("READY");
}

void loop() {
  // Serial einlesen
  while (Serial.available()) {
    char c = (char)Serial.read();
    if (c == '\r') continue;
    if (c == '\n') {
      handleLine(rxLine);
      rxLine = "";
    } else {
      if (rxLine.length() < 500) rxLine += c; // bei langen Pulsfolgen ruhig erhöhen
      else { rxLine = ""; replyERR("line too long"); }
    }
  }

  // Pulsfolge abspielen
  updatePulse();
}
