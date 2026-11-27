# Third-Party Notices

This file covers third-party data and licenses associated with
`assets/dictionary/dict-en-zh.sqlite` in the Thanslator repository.

## ECDICT

- Project: ECDICT - Free English to Chinese Dictionary Database
- Repository: https://github.com/skywind3000/ECDICT
- Data file: https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv
- Repository license: MIT
- Copyright (c) 2025 Linwei

The dictionary database in this directory is a transformed subset of
ECDICT's `ecdict.csv` (only the `word`, `phonetic`, `translation`, `bnc`,
`frq`, `collins`, `oxford`, and `exchange` fields are retained). The
transformation is performed by `tools/DictionaryBuilder/build_ecdict.py`.

### MIT license text (ECDICT)

```text
MIT License

Copyright (c) 2025 Linwei

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Data-provenance caveat

ECDICT's MIT license covers ECDICT's own repository as published. ECDICT is a
compilation, and its README describes incorporating data from earlier sources,
including:

- **cdict-1.0-1.rpm** - described by ECDICT as an "open source dictionary"
  obtained for Linux. cdict is commonly reported or packaged as GPL-licensed,
  but ECDICT does not reproduce a cdict license text or clarify how that
  license interacts with ECDICT's MIT release. Treat cdict-derived material as
  GPL-claimed and potentially subject to GPL obligations.
- **屌丝字典 (diaosi)** - https://github.com/fxsjy/diaosi. ECDICT's HISTORY
  entry for 2017-04-07 says the English-Chinese portion was incorporated.
  ECDICT does not state a license for that contributed data. Treat it as
  unknown provenance and unknown license.

The MIT grant above therefore does not automatically clear third-party
upstream rights in the data. Redistribution of this database, especially
commercially or through an app store, may carry obligations or risks not
resolved by the MIT text. The database is provided for local/personal use with
attribution, without any claim of legal clearance. If that risk is
unacceptable, replace the data source or obtain permission from the relevant
rights holders.

This notice is factual provenance information, not legal advice.

## Thanslator build tooling

`tools/DictionaryBuilder/build_ecdict.py` is part of the Thanslator project.
It uses only the Python standard library. No third-party Python packages are
bundled or required.
