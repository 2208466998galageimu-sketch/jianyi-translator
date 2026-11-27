#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <iostream>
#include <memory>
#include <regex>
#include <stdexcept>
#include <string>
#include <vector>

#include <ctranslate2/translator.h>
#include <sentencepiece_processor.h>

namespace {

struct Options {
  std::string model_dir;
  std::string source_spm;
  std::string target_spm;
  std::string pipe_name = "Thanslator.LocalMt";
  int threads = 4;
  std::string architecture = "marian";
  std::string source_language = "en";
  std::string target_language = "zh-Hans";
  std::string source_prefix = ">>cmn_Hans<<";
  bool append_source_eos = false;
};

std::string ToUtf8(const std::wstring& value) {
  if (value.empty()) return {};
  const int size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                                       nullptr, 0, nullptr, nullptr);
  std::string result(size, '\0');
  WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                      result.data(), size, nullptr, nullptr);
  return result;
}

std::wstring ToWide(const std::string& value) {
  if (value.empty()) return {};
  const int size = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                                       nullptr, 0);
  std::wstring result(size, L'\0');
  MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                      result.data(), size);
  return result;
}

int CountCjk(const std::string& utf8) {
  int count = 0;
  for (size_t i = 0; i < utf8.size();) {
    const unsigned char c = static_cast<unsigned char>(utf8[i]);
    uint32_t cp = 0;
    if (c < 0x80) {
      cp = c;
      i += 1;
    } else if ((c & 0xE0) == 0xC0 && i + 1 < utf8.size()) {
      cp = (static_cast<uint32_t>(c & 0x1F) << 6) |
           (static_cast<unsigned char>(utf8[i + 1]) & 0x3F);
      i += 2;
    } else if ((c & 0xF0) == 0xE0 && i + 2 < utf8.size()) {
      cp = (static_cast<uint32_t>(c & 0x0F) << 12) |
           ((static_cast<unsigned char>(utf8[i + 1]) & 0x3F) << 6) |
           (static_cast<unsigned char>(utf8[i + 2]) & 0x3F);
      i += 3;
    } else if ((c & 0xF8) == 0xF0 && i + 3 < utf8.size()) {
      cp = (static_cast<uint32_t>(c & 0x07) << 18) |
           ((static_cast<unsigned char>(utf8[i + 1]) & 0x3F) << 12) |
           ((static_cast<unsigned char>(utf8[i + 2]) & 0x3F) << 6) |
           (static_cast<unsigned char>(utf8[i + 3]) & 0x3F);
      i += 4;
    } else {
      i += 1;
      continue;
    }

    if ((cp >= 0x3400 && cp <= 0x4DBF) ||
        (cp >= 0x4E00 && cp <= 0x9FFF) ||
        (cp >= 0xF900 && cp <= 0xFAFF)) {
      ++count;
    }
  }
  return count;
}

bool IsSaneOutput(const std::string& utf8, bool require_cjk = true) {
  const int cjk = CountCjk(utf8);
  if (utf8.empty() || (require_cjk && cjk == 0)) return false;
  if (utf8.find(">>") != std::string::npos ||
      utf8.find("<<") != std::string::npos) return false;

  // A long output containing only one Han character is usually mojibake
  // (e.g. "3⁄4 ̄ ̧漯B"), not a short valid translation. Very short outputs
  // such as "是" are still accepted.
  if (require_cjk && utf8.size() > 6 && cjk < 2) return false;

  // Reject replacement characters and obvious non-text control bytes.
  for (unsigned char c : utf8) {
    if (c < 0x20 && c != '\n' && c != '\r' && c != '\t') return false;
  }
  return utf8.find("\xEF\xBF\xBD") == std::string::npos;
}

std::string Cleanup(const std::string& text) {
  std::string result = std::regex_replace(
      text, std::regex("\\s+([，。！？；：])"), "$1");
  while (!result.empty() && (result.back() == ' ' || result.back() == '\t' ||
                             result.back() == '\r' || result.back() == '\n')) {
    result.pop_back();
  }
  return result;
}

class LocalEngine {
 public:
  LocalEngine(const Options& options) : options_(options) {
    auto status = source_sp_.Load(options.source_spm);
    if (!status.ok()) {
      throw std::runtime_error("cannot load source SentencePiece model: " + status.ToString());
    }
    status = target_sp_.Load(options.target_spm);
    if (!status.ok()) {
      throw std::runtime_error("cannot load target SentencePiece model: " + status.ToString());
    }

    ctranslate2::ReplicaPoolConfig pool_config;
    pool_config.num_threads_per_replica = static_cast<size_t>(std::max(1, options.threads));
    translator_ = std::make_unique<ctranslate2::Translator>(
        options.model_dir,
        ctranslate2::Device::CPU,
        options.architecture == "m2m100" ? ctranslate2::ComputeType::INT8 : ctranslate2::ComputeType::FLOAT32,
        std::vector<int>{0},
        false,
        pool_config);
  }

  std::string Translate(const std::string& text) {
    std::vector<std::string> source_tokens;
    auto status = source_sp_.Encode(text, &source_tokens);
    if (!status.ok()) {
      throw std::runtime_error("source tokenization failed: " + status.ToString());
    }
    if (options_.architecture == "m2m100")
      source_tokens.insert(source_tokens.begin(), LanguageToken(options_.source_language));
    else if (!options_.source_prefix.empty())
      source_tokens.insert(source_tokens.begin(), options_.source_prefix);
    if (options_.append_source_eos) source_tokens.push_back("</s>");
    if (source_tokens.size() > 512) {
      throw std::runtime_error("input_too_long");
    }

    auto options = MakeOptions(4, 4);
    const std::vector<std::vector<std::string>> batch{source_tokens};
    const std::vector<std::vector<std::string>> prefix = options_.architecture == "m2m100"
        ? std::vector<std::vector<std::string>>{{LanguageToken(options_.target_language)}}
        : std::vector<std::vector<std::string>>{{}};
    auto results = translator_->translate_batch(batch, prefix, options);
    if (results.empty()) {
      throw std::runtime_error("translator returned no result");
    }

    for (const auto& hypothesis : results[0].hypotheses) {
      if (hypothesis.size() >= options.max_decoding_length) continue;
      std::string decoded;
      status = Decode(hypothesis, &decoded);
      if (!status.ok()) continue;
      if (IsSaneOutput(decoded, options_.architecture != "m2m100")) {
        return Cleanup(decoded);
      }
    }

    // Retry with greedy decoding if beam hypotheses are all malformed.
    options = MakeOptions(1, 1);
    auto fallback = translator_->translate_batch(batch, prefix, options);
    if (!fallback.empty()) {
      for (const auto& hypothesis : fallback[0].hypotheses) {
        if (hypothesis.size() >= options.max_decoding_length) continue;
        std::string decoded;
        status = Decode(hypothesis, &decoded);
        if (status.ok() && IsSaneOutput(decoded, options_.architecture != "m2m100")) return Cleanup(decoded);
      }
    }

    throw std::runtime_error("invalid_translation");
  }

 private:
  static std::string LanguageToken(const std::string& code) {
    return "__" + (code == "zh-Hans" ? std::string("zh") : code) + "__";
  }
  sentencepiece::util::Status Decode(const std::vector<std::string>& hypothesis, std::string* output) {
    auto tokens = hypothesis;
    if (options_.architecture == "m2m100" && !tokens.empty() && tokens.front() == LanguageToken(options_.target_language))
      tokens.erase(tokens.begin());
    if (!tokens.empty() && tokens.back() == "</s>") tokens.pop_back();
    return target_sp_.Decode(tokens, output);
  }
  static ctranslate2::TranslationOptions MakeOptions(size_t beam_size, size_t hypotheses) {
    ctranslate2::TranslationOptions options;
    options.beam_size = beam_size;
    options.num_hypotheses = hypotheses;
    options.max_decoding_length = 512;
    options.max_input_length = 0;  // Never silently truncate; the caller splits and we validate above.
    options.return_scores = true;
    options.replace_unknowns = true;
    return options;
  }

  Options options_;
  sentencepiece::SentencePieceProcessor source_sp_;
  sentencepiece::SentencePieceProcessor target_sp_;
  std::unique_ptr<ctranslate2::Translator> translator_;
};

Options ParseOptions(int argc, char** argv) {
  Options options;
  for (int i = 1; i < argc; ++i) {
    const std::string arg = argv[i];
    const auto next = [&]() -> std::string {
      if (i + 1 >= argc) throw std::runtime_error("missing value for " + arg);
      return argv[++i];
    };
    if (arg == "--model") options.model_dir = next();
    else if (arg == "--source-spm") options.source_spm = next();
    else if (arg == "--target-spm") options.target_spm = next();
    else if (arg == "--architecture") options.architecture = next();
    else if (arg == "--source-language") options.source_language = next();
    else if (arg == "--target-language") options.target_language = next();
    else if (arg == "--source-prefix") options.source_prefix = next();
    else if (arg == "--append-source-eos") options.append_source_eos = next() == "true";
    else if (arg == "--pipe") options.pipe_name = next();
    else if (arg == "--threads") options.threads = std::stoi(next());
    else if (arg == "--help") {
      std::cout << "Thanslator.LocalMtHost --model DIR --source-spm FILE --target-spm FILE "
                   "[--pipe NAME] [--threads N]\n";
      std::exit(0);
    } else {
      throw std::runtime_error("unknown argument: " + arg);
    }
  }

  if (options.model_dir.empty() || options.source_spm.empty() || options.target_spm.empty()) {
    throw std::runtime_error("--model, --source-spm and --target-spm are required");
  }
  return options;
}

bool ReadExact(HANDLE pipe, void* buffer, DWORD size) {
  auto* bytes = static_cast<char*>(buffer);
  DWORD total = 0;
  while (total < size) {
    DWORD read = 0;
    if (!ReadFile(pipe, bytes + total, size - total, &read, nullptr) || read == 0) {
      return false;
    }
    total += read;
  }
  return true;
}

bool WriteExact(HANDLE pipe, const void* buffer, DWORD size) {
  const auto* bytes = static_cast<const char*>(buffer);
  DWORD total = 0;
  while (total < size) {
    DWORD written = 0;
    if (!WriteFile(pipe, bytes + total, size - total, &written, nullptr) || written == 0) {
      return false;
    }
    total += written;
  }
  return true;
}

void Serve(const Options& options, LocalEngine& engine) {
  const std::wstring pipe_path = L"\\\\.\\pipe\\" + ToWide(options.pipe_name);
  HANDLE pipe = CreateNamedPipeW(
      pipe_path.c_str(),
      PIPE_ACCESS_DUPLEX,
      PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
      1,
      64 * 1024,
      64 * 1024,
      0,
      nullptr);
  if (pipe == INVALID_HANDLE_VALUE) {
    throw std::runtime_error("CreateNamedPipeW failed: " +
                             std::to_string(GetLastError()));
  }

  for (;;) {
    const BOOL connected = ConnectNamedPipe(pipe, nullptr);
    if (!connected && GetLastError() != ERROR_PIPE_CONNECTED) {
      break;
    }

    for (;;) {
      uint32_t request_size = 0;
      if (!ReadExact(pipe, &request_size, sizeof(request_size))) break;
      if (request_size == 0 || request_size > 1024 * 1024) break;

      std::string request(request_size, '\0');
      if (!ReadExact(pipe, request.data(), request_size)) break;

      std::string response;
      try {
        response = engine.Translate(request);
      } catch (const std::exception& ex) {
        response = "__ERROR__:" + std::string(ex.what());
      }

      const uint32_t response_size = static_cast<uint32_t>(response.size());
      if (!WriteExact(pipe, &response_size, sizeof(response_size)) ||
          !WriteExact(pipe, response.data(), response_size)) {
        break;
      }
      FlushFileBuffers(pipe);
    }

    DisconnectNamedPipe(pipe);
  }

  CloseHandle(pipe);
}

}  // namespace

int main(int argc, char** argv) {
  try {
    SetConsoleOutputCP(CP_UTF8);
    const Options options = ParseOptions(argc, argv);
    LocalEngine engine(options);
    std::cout << "READY" << std::endl;
    Serve(options, engine);
    return 0;
  } catch (const std::exception& ex) {
    std::cerr << "FATAL: " << ex.what() << std::endl;
    return 1;
  }
}
