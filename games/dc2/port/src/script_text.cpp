#include "script_text.hpp"

#include <array>
#include <cstring>
#include <limits>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "log/log.hpp"
#include "runscript.hpp"
#include "script_timing.hpp"

namespace script_text {
namespace {

/** Offset of the compiled script code-section field. */
constexpr std::size_t kCodeField = offsetof(RS_PROG_HEADER, code);
/** Byte extent of a retail script instruction. */
constexpr std::size_t kInsnSize = sizeof(vmcode_t);

/** Spanish Spheda tutorial strings using the PAL terminology and encoded glyphs. */
constexpr std::array<Line, 19> kS17Spanish{
    {
     {.push = 0xCDC4, .retail = "Si ferma con il pulsante (O).", .text = "Se para con el bot[UNI00f3]n (O)."},
     {.push = 0x18A04, .retail = "Prepara il Ramo maneggevole.", .text = "Equipa el Bast[UNI00f3]n Multiusos."},
     {.push = 0x1AB0C, .retail = "Questa [UNI00e8] l'ultima volta che potrai\navvicinare la Sphera.", .text = "Esta es la [UNI00fa]ltima vez que\npodr[UNI00e1]s mover la Esfera hacia ti."},
     {.push = 0x1ABFC, .retail = "Avvicinare la Sphera?", .text = "[UNI00bf]Acercar la Esfera?"},
     {.push = 0x2DB10, .retail = "Vedi se trovi un Ramo\nmaneggevole. Cos[UNI00ec], potr[UNI00f2]\nmostrarti come colpire la Sphera.", .text = "Mira a ver si hay un palo por ah[UNI00ed]\ncerca. Despu[UNI00e9]s te ense[UNI00f1]ar[UNI00e9]\na golpear la Esfera."},
     {.push = 0x311D8, .retail = "S[UNI00ec]\nNo", .text = "S[UNI00ed]\nNo"},
     {.push = 0x32560, .retail = "Come?\nVuoi rinunciare?", .text = "[UNI00bf]C[UNI00f3]mo?\n[UNI00bf]Te rindes?"},
     {.push = 0x32620, .retail = "S[UNI00ec]\nNo", .text = "S[UNI00ed]\nNo"},
     {.push = 0x32818, .retail = "Si.\nBuona fortuna, Max!", .text = "S[UNI00ed].\n[UNI00a1]Buena suerte, Max!"},
     {.push = 0x33004, .retail = "Oh, che peccato!\nVuoi riprovare?", .text = "[UNI00a1]Oh, qu[UNI00e9] pena!\n[UNI00bf]Quieres volver a intentarlo?"},
     {.push = 0x331CC, .retail = "S[UNI00ec]\nNo", .text = "S[UNI00ed]\nNo"},
     {.push = 0x334FC, .retail = "Sei un disastro, Max!\nLascia fare a me!", .text = "[UNI00a1]Eres un desastre, Max!\n[UNI00a1]D[UNI00e9]jame a m[UNI00ed]!"},
     {.push = 0x44190,
         .retail = "\"Tiro\"\n1) Mira con la levetta analogica sx\n2) Premi (O) per oscillare la mazza\n3) Premi ancora (O) per la potenza\n4) "
                   "Premi (O) di nuovo per il tiro",
         .text = "\"Lanzamiento\"\n1) Apunta con el joystick izquierdo\n2) Pulsa (O) para empezar el golpe\n3) Pulsa (O) de nuevo para la "
                 "potencia\n4) Pulsa (O) otra vez para golpear"},
     {.push = 0x44220,
         .retail = "\"Zona d'Impatto\"\nLa barra rossa sotto l'indicatore\ndi potenza. Centrando la met[UNI00e0] della\nbarra, si ottiene un "
                   "colpo forte.",
         .text = "\"Zona de Impacto\"\nEs la barra roja bajo el indicador de\npotencia. Golpea en el centro de la\nZona de Impacto para un "
                 "golpe fuerte."},
     {.push = 0x442B0, .retail = "Se colpisci da un punto esterno\nalla Zona d'Impatto, la Sphera\nvoler[UNI00e0] nella direzione sbagliata.", .text = "Si golpeas fuera de la Zona de Impacto,\nla Esfera saldr[UNI00e1] volando en la\ndirecci[UNI00f3]n equivocada."},
     {.push = 0x44340,
         .retail = "\"Cambiamento Punto d'Impatto\"\nPer cambiare il punto d'impatto\ndella Sphera, punta la levetta\nanalogica sx in una "
                   "direzione.<page>Modificando cos[UNI00ec] quanto segue:",
         .text = "\"Cambiar el punto de impacto\"\nSi mantienes el joystick izquierdo en\nuna direcci[UNI00f3]n al golpear, cambiar[UNI00e1]s\nla "
                 "parte de la Esfera que golpeas.<page>Esto produce los efectos siguientes:"},
     {.push = 0x443D0,
         .retail = "Su: Topspin per palla veloce.\nGi[UNI00f9]: Backspin per frenare palla.\nSx: Slice e deviazione a destra.\nDx: Hook e "
                   "deviazione a sinistra.",
         .text = "Arriba: liftado, rueda con facilidad.\nAbajo: cortado, frena con facilidad.\nIzq.: golpe que curva a la derecha.\nDcha.: golpe "
                 "que curva a la izquierda."},
     {.push = 0x44460, .retail = "Se la Sphera finisce in un punto\nirraggiungibile, premi (#)per\navvicinare a te la Sphera.", .text = "Si la Esfera cae en un lugar al que\nno puedes llegar, pulsa (#) para\nacercar la Esfera hacia ti."},
     {.push = 0x444F0, .retail = "[(!)]Spheda [UNI00e8] stato aggiunto alla sezione\n   Guida del Menu principale.", .text = "[(!)]Esfeda se ha a[UNI00f1]adido a la secci[UNI00f3]n\n   Ayuda del men[UNI00fa] Principal."},
     }
};

/** Spanish f04 menus with one label for each jump gate destination. */
constexpr std::array<Line, 2> kF04Spanish{
    {
     {.push = 0x2CD20,
         .retail = "Selecciona Destino de la Puerta de salto\nPuerta de salto Norte\nPuerta de salto Este -- Oeste\nEnfrente de la Puerta de "
                   "salto\nPuente de Paznos",
         .text = "Selecciona Destino de la Puerta de salto\nPuerta de salto Norte\nPuerta de salto Este\nPuerta de salto Oeste\nEnfrente de "
                 "la Puerta del tiempo\nPuente de Paznos"},
     {.push = 0x2CD98,
         .retail = "Selecciona Destino de la Puerta de salto\nPuerta de salto Norte\nPuerta de salto Este -- Oeste\nEnfrente de la Puerta de "
                   "salto\n",
         .text = "Selecciona Destino de la Puerta de salto\nPuerta de salto Norte\nPuerta de salto Este\nPuerta de salto Oeste\nEnfrente de "
                 "la Puerta del tiempo"},
     }
};

/** Spanish s30 menu with separate east and west jump gate destinations. */
constexpr std::array<Line, 1> kS30Spanish{
    {
     {.push = 0x2FC94,
         .retail = "Selecciona Destino de la Puerta de salto\nPuerta de salto Norte\nPuerta de salto Este -- Oeste\nEnfrente de la Puerta de "
                   "salto\n",
         .text = "Selecciona Destino de la Puerta de salto\nPuerta de salto Norte\nPuerta de salto Este\nPuerta de salto Oeste\nEnfrente de "
                 "la Puerta del tiempo"},
     }
};

/** Latin-1 Spanish lesson corrections describing the sphere's red and blue colour rules. */
constexpr std::array<Edit, 3> kE314Spanish{
    {
     {.retail = "No tienes que acordarte de nada m\xe1s.", .text = "Tienes que acordarte de otra cosa m\xe1s."},
     {.retail = "De azul a rojo, de negro a azul.", .text = "De azul a rojo, de rojo a azul."},
     {.retail = "cambiar\xe1 el color a negro.", .text = "cambiar\xe1 el color."},
     }
};

/**
 * Associates an event message file with its exact text corrections.
 */
struct Messages {
    const char           *path;  /**< Root-relative message filename. */
    std::span<const Edit> edits; /**< Corrections required for this file. */
};

/** Event files with exact passage corrections. */
constexpr std::array<Messages, 1> kMessages{
    {
     {.path = "event/e300/e314/e314_5.txt", .edits = kE314Spanish},
     }
};

/**
 * Associates a compiled script with its anchored string corrections.
 */
struct Script {
    const char           *path;  /**< Root-relative script filename. */
    std::span<const Line> lines; /**< Corrections required for this script. */
};

/** Compiled scripts with anchored string corrections. */
constexpr std::array<Script, 3> kScripts{
    {
     {.path = "map/s/s17/s17_5.stb", .lines = kS17Spanish},
     {.path = "map/f/f04/f04_5.stb", .lines = kF04Spanish},
     {.path = "map/s/s30/s30_5.stb", .lines = kS30Spanish},
     }
};

/**
 * Reads an unaligned signed script operand.
 */
std::int32_t Read32(const unsigned char *bytes, std::size_t offset) {
    std::int32_t value{};
    std::memcpy(&value, bytes + offset, sizeof(value));
    return value;
}

/**
 * Writes an unaligned signed script operand.
 */
void Write32(unsigned char *bytes, std::size_t offset, std::int32_t value) {
    std::memcpy(bytes + offset, &value, sizeof(value));
}

/**
 * Identifies replacements that require appended storage.
 */
bool Appended(const Line &line) {
    return std::strlen(line.text) > std::strlen(line.retail);
}

/**
 * Finds the exact retail string referenced by an expected instruction.
 */
std::optional<std::size_t> RetailString(const unsigned char *bytes, std::size_t size, std::size_t code, const Line &line) {
    if (line.push + kInsnSize > size || Read32(bytes, line.push) != RS_OP_PUSH_CONST || Read32(bytes, line.push + 4) != RS_CONST_STR) {
        return std::nullopt;
    }
    const std::int32_t relative = Read32(bytes, line.push + 8);
    const std::size_t  length = std::strlen(line.retail);
    if (relative < 0 || code + static_cast<std::size_t>(relative) + length >= size) {
        return std::nullopt;
    }
    const std::size_t at = code + static_cast<std::size_t>(relative);
    if (std::memcmp(bytes + at, line.retail, length) != 0 || bytes[at + length] != 0) {
        return std::nullopt;
    }
    return at;
}

/**
 * Finds event message corrections by normalized asset path.
 */
std::span<const Edit> FindEdits(std::string_view key) {
    const std::string normalised = script_timing::Key(key);
    for (const Messages &messages : kMessages) {
        if (normalised == messages.path) {
            return messages.edits;
        }
    }
    return {};
}

/**
 * Returns corrected event text only when every expected passage occurs exactly once.
 */
std::optional<std::string> Edited(std::string text, std::span<const Edit> edits) {
    for (const Edit &edit : edits) {
        const std::size_t at = text.find(edit.retail);
        if (at == std::string::npos || text.find(edit.retail, at + 1) != std::string::npos) {
            return std::nullopt;
        }
        text.replace(at, std::strlen(edit.retail), edit.text);
    }
    return text;
}

/**
 * Reports the first correction result for each asset.
 */
void LogOnce(std::string_view key, bool matched) {
    static std::mutex            mutex;
    static std::set<std::string> logged;
    std::scoped_lock             lock(mutex);
    if (!logged.emplace(key).second) {
        return;
    }
    if (matched) {
        CT_LOG_INFO(Script, "script text: {}: Spanish text", key);
    } else {
        CT_LOG_WARN(Script, "script text: {} does not match the PAL file; its text is left unchanged", key);
    }
}

} // namespace

std::span<const Line> Find(std::string_view key) {
    const std::string normalised = script_timing::Key(key);
    for (const Script &script : kScripts) {
        if (normalised == script.path) {
            return script.lines;
        }
    }
    return {};
}

std::span<const Edit> Edits(std::string_view key) {
    return FindEdits(key);
}

std::size_t Extra(std::string_view key) {
    std::ptrdiff_t growth = 0;
    for (const Edit &edit : FindEdits(key)) {
        growth += static_cast<std::ptrdiff_t>(std::strlen(edit.text)) - static_cast<std::ptrdiff_t>(std::strlen(edit.retail));
    }
    std::size_t extra = growth > 0 ? static_cast<std::size_t>(growth) : 0;
    for (const Line &line : Find(key)) {
        if (Appended(line)) {
            extra += std::strlen(line.text) + 1;
        }
    }
    return extra;
}

std::size_t Patch(std::string_view key, unsigned char *bytes, std::size_t size) {
    if (const auto edits = FindEdits(key); !edits.empty()) {
        const auto text = Edited(std::string(reinterpret_cast<const char *>(bytes), size), edits);
        if (!text) {
            LogOnce(key, false);
            return size;
        }
        std::memcpy(bytes, text->data(), text->size());
        if (text->size() < size) {
            std::memset(bytes + text->size(), 0, size - text->size());
        }
        LogOnce(key, true);
        return text->size();
    }

    const auto lines = Find(key);
    if (lines.empty()) {
        return size;
    }
    constexpr auto max_size = static_cast<std::size_t>(std::numeric_limits<std::int32_t>::max());
    if (size > max_size || Extra(key) > max_size - size || size < kCodeField + sizeof(std::int32_t) || Read32(bytes, kCodeField) < 0) {
        LogOnce(key, false);
        return size;
    }
    const auto code = static_cast<std::size_t>(Read32(bytes, kCodeField));

    std::vector<std::size_t> strings(lines.size());
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const auto at = RetailString(bytes, size, code, lines[i]);
        if (!at) {
            LogOnce(key, false);
            return size;
        }
        strings[i] = *at;
    }

    std::size_t end = size;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const Line       &line = lines[i];
        const std::size_t length = std::strlen(line.text) + 1;
        if (!Appended(line)) {
            std::memcpy(bytes + strings[i], line.text, length);
            continue;
        }
        std::memcpy(bytes + end, line.text, length);
        Write32(bytes, line.push + 8, static_cast<std::int32_t>(end - code));
        end += length;
    }
    LogOnce(key, true);
    return end;
}

} // namespace script_text
