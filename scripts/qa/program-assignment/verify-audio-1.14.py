"""Verifica en lectura S3 los 13 objetos aprobados, con decodificación completa local.

Requiere imageio-ffmpeg instalado fuera del repo (PYTHONPATH); no guarda audio en evidencia.
"""
import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile

import boto3
import imageio_ffmpeg

backup = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
s3 = boto3.Session(profile_name='default', region_name='us-east-2').client('s3')
results = []
for media in backup['catalog']:
    with tempfile.TemporaryDirectory(prefix='qa-114-audio-') as temp:
        audio = pathlib.Path(temp) / 'audio.mp3'
        s3.download_file('cooppadresd-storage-prod', media['key'], str(audio))
        assert audio.stat().st_size == media['bytes']
        check = subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), '-v', 'error', '-xerror', '-i', str(audio), '-f', 'null', '-'],
                               capture_output=True, timeout=90)
        assert check.returncode == 0 and not check.stderr, f"Audio inválido: episodio {media['episode']}"
        results.append(dict(id=media['id'], episode=media['episode'], bytes=audio.stat().st_size,
                            sha256=hashlib.sha256(audio.read_bytes()).hexdigest(), fullDecodeExitCode=check.returncode))
result = dict(readOnlyStorage=True, files=results, valid=len(results), uploads=0)
with pathlib.Path(sys.argv[2]).open('x', encoding='utf-8') as output:
    json.dump(result, output, indent=2); output.write('\n')
print(json.dumps(dict(valid=len(results), fullDecodeErrors=0, uploads=0)))
