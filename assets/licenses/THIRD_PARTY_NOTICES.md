# Third-party notices

## ECDICT

- Repository: https://github.com/skywind3000/ECDICT
- License file: MIT License, Copyright (c) 2025 Linwei
- Data: `assets/dictionary/dict-en-zh.sqlite` is built from ECDICT's `ecdict.csv`.
- Provenance caveat: ECDICT's README lists upstream material including cdict
  (historically labeled GPL) and 《屌丝字典》 (license unknown). ECDICT does not
  state a separate data license. Treat redistribution of the dictionary data as
  unresolved until reviewed.

MIT License text (repository): permission is granted, free of charge, to use,
copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
software, subject to inclusion of the copyright and permission notice and the
software being provided "as is", without warranty.

## Local translation runtime

- CTranslate2 4.8.5: https://github.com/OpenNMT/CTranslate2 (MIT, see CTranslate2-LICENSE.txt).
- SentencePiece 0.2.0: https://github.com/google/sentencepiece (Apache-2.0, see SentencePiece-LICENSE.txt and included third-party notices).
- M2M100 optional download: https://huggingface.co/facebook/m2m100_418M (model card: MIT); pinned CTranslate2 INT8 distribution https://huggingface.co/jncraton/m2m100_418M-ct2-int8/tree/7c1b2620a4e58dacecbd8bf89cfd6da7eb9eb7b0. See M2M100-fairseq-LICENSE.txt. Weights are not bundled in the installer.

## OCR optional profiles

- Official RapidOCR model catalog: https://github.com/RapidAI/RapidOCR; pinned catalog revision is recorded with QA evidence. Download weights and character dictionaries come from RapidAI/RapidOCR ModelScope v3.10.0. See RapidOCR-LICENSE.txt (Apache-2.0).
- OCR runtime and its adapted MinerU code retain their own complete notices in OcrHost. MinerU's license includes additional conditions; it must not be described as an unmodified Apache-2.0 license.
- The existing ECDICT data provenance caveat above remains unresolved; this release is not a declaration that all bundled dictionary data has cleared redistribution review.

## Other optional downloads

- User-initiated local LLM downloads retain upstream model licenses and are not included in the installer. No user's uploaded weights are relicensed or modified.
