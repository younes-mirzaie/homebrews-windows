"""Turns webpage.html into webpage.h (a C string). Run after editing the page:
   python3 gen-webpage.py"""
import os
here = os.path.dirname(os.path.abspath(__file__))
html = open(os.path.join(here, 'webpage.html'), encoding='utf-8').read()
out = ['/* generated from webpage.html by gen-webpage.py; do not edit */', 'static const char kWebPage[] =']
for line in html.split('\n'):
    b = (line + '\n').encode('utf-8')
    s = ''.join(chr(c) if 32 <= c < 127 and c not in (34, 92, 63) else '\\%03o' % c for c in b)
    out.append('    "' + s + '"')
out[-1] += ';'
open(os.path.join(here, 'webpage.h'), 'w').write('\n'.join(out) + '\n')
