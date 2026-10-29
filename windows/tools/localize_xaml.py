"""Bind explicitly owned UI attributes to the embedded bilingual catalogue.

Editable text and the assistant's name are data, so they are never rewritten.
No user files, resource values, handlers, control names or command tags are edited.
"""
import hashlib,html,json,re
from pathlib import Path

root=Path(__file__).resolve().parents[1]/'app/VoiceAssistant.Desktop'
catalog=json.loads((root/'Localization/Ui.en.json').read_text(encoding='utf-8'))
tags=re.compile(r'<(?P<tag>[\w:]+)(?P<attrs>(?:[^>\"\']|\"[^\"]*\"|\'[^\']*\')*)>')
attributes=re.compile(r'(?P<name>\b(?:Text|Content|Title|ToolTip|Header|AutomationProperties.Name))="(?P<value>[^"]*)"')
def key(value): return 'Ui.'+hashlib.sha256(value.encode('utf-8')).hexdigest()[:24].upper()
count=0
for path in root.glob('*.xaml'):
    if path.name=='App.xaml': continue
    def tag_replace(tag):
        def attr_replace(attr):
            global count
            value=html.unescape(attr['value'])
            if value not in catalog: return attr[0]
            if tag['tag']=='TextBox' and attr['name']=='Text': return attr[0]
            if 'x:Name="BrandNameText"' in tag['attrs'] and attr['name']=='Text': return attr[0]
            count+=1
            return attr['name']+'="{DynamicResource '+key(value)+'}"'
        return '<'+tag['tag']+attributes.sub(attr_replace,tag['attrs'])+'>'
    old=path.read_text(encoding='utf-8-sig')
    new=tags.sub(tag_replace,old)
    if new!=old: path.write_text(new,encoding='utf-8',newline='\n')
print(f'Localized {count} fixed UI attributes; data and command IDs preserved.')
