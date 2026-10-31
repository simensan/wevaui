"""Create an owned Agentic runtime audit project from prepared survival fixtures.

Requires the font/PNG fixtures recorded by agentic-packed-20261030.md.
Does not modify an existing game or select an engine globally.
"""
import argparse
import json
import re
import shutil
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('fixtures', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--direct', action='store_true')
    parser.add_argument('--viewport', type=int, nargs=2, default=[1280, 720],
                        metavar=('WIDTH', 'HEIGHT'))
    parser.add_argument('--defer-ui', action='store_true',
                        help='Start without a UI; first key 2-7 loads it for cold-open profiling')
    args = parser.parse_args()
    if args.viewport[0] < 1280 or args.viewport[1] < 720:
        parser.error('survival fixture requires at least 1280x720')
    source = args.fixtures.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    ui = output / 'assets/Ui'
    ui.mkdir(parents=True)
    scripts = output / 'assets/Scripts'
    scripts.mkdir()
    scenes = output / 'assets/Scenes'
    scenes.mkdir()
    for file in source.iterdir():
        if file.suffix in ('.png', '.ttf'):
            shutil.copy2(file, ui / file.name)
    html = (source / 'inventory.html').read_text(encoding='utf-8')
    css = (source / 'inventory.css').read_text(encoding='utf-8')
    html = html.replace(source.as_posix() + '/', '')
    html = html.replace('</img>', '')
    css = css.replace(source.as_posix() + '/', '')
    html = html.replace('class="survival menu-open"', 'class="survival {{MenuClass}}"')
    css += '\n.survival:not(.menu-open) .satchel,.survival:not(.menu-open) .scrim{display:none}\n'

    def text_binding(element_id, key):
        nonlocal html
        pattern = r'(<[^>]+\bid="' + re.escape(element_id) + r'"[^>]*>)[^<]*(</[^>]+>)'
        html, count = re.subn(pattern, lambda m: m[1] + '{{' + key + '}}' + m[2], html)
        if count != 1:
            raise ValueError(f'Expected one text target: {element_id}, got {count}')

    def attribute_binding(element_id, attribute, value):
        nonlocal html
        pattern = r'<[^>]+\bid="' + re.escape(element_id) + r'"[^>]*>'
        def rewrite(match):
            tag = re.sub(r'\s+' + re.escape(attribute) + r'="[^"]*"', '', match[0])
            return tag[:-1] + f' {attribute}="{value}">'
        html, count = re.subn(pattern, rewrite, html)
        if count != 1:
            raise ValueError(f'Expected one attribute target: {element_id}, got {count}')

    keys = {'item-name': 'Name', 'item-category': 'Category', 'item-description': 'Description',
            'item-weight': 'Weight', 'item-quantity': 'Quantity', 'item-action': 'Action'}
    for element, key in keys.items():
        text_binding(element, key)
    text_binding('stamina-value', 'Stamina')
    attribute_binding('stamina-fill', 'style', 'width:{{Stamina}}%')
    attribute_binding('detail-icon', 'src', '{{Icon}}')
    attribute_binding('item-action', 'disabled', '{{Disabled}}')
    for i in range(12):
        attribute_binding(f'item-{i}', 'class', '{{Slot' + str(i) + '}}')
        attribute_binding(f'item-{i}', 'on-click', f'select_{i}')
    (ui / 'main.html').write_text(html, encoding='utf-8')
    (ui / 'main.css').write_text(css, encoding='utf-8')

    states = json.loads((source / 'mutations.json').read_text(encoding='utf-8'))[1]
    select = []
    for i, state in enumerate(states):
        lines = []
        for mutation in state:
            element = mutation['selector'].removeprefix('#')
            attribute = mutation.get('attribute')
            value = mutation['value']
            if element.startswith('item-') and attribute == 'class':
                key = 'Slot' + element.removeprefix('item-')
            elif element == 'detail-icon' and attribute == 'src':
                key, value = 'Icon', Path(value).name
            elif attribute == 'disabled':
                key, value = 'Disabled', 'false' if value is None else 'true'
            elif element in keys and attribute is None:
                key = keys[element]
            else:
                raise ValueError(f'Unsupported fixture mutation: {mutation}')
            lines.append(f'engine.html_set({json.dumps(key)}, {json.dumps(value)});')
        select.append('if (index == ' + str(i) + ') {' + ''.join(lines) + '}')
    driver = '''
let mode:i32=4;let ticks:i32=0;let selected:i32=0;let clicks:i32=0;let uses:i32=0;let reloads:i32=0;
let loaded:bool=false;
function select(index:i32):void {
selected=index;
SELECT_BODY
engine.set_global("audit_selected",selected);
}
function setMode(value:i32):void {
if(value!=0 && !loaded){engine.html_load("assets/Ui/main.html");loaded=true;}
mode=value;engine.html_show(mode!=0 && mode!=6);
engine.html_set("MenuClass",mode>=3?"menu-open":"");
engine.html_set("Stamina","100");select(selected);
engine.set_global("audit_mode",mode);
}
export function init(entity:i64):void {setMode(INITIAL_MODE);}
export function update(entity:i64,dt:f64):void {
ticks++;
if(engine.get_key_down("1")){setMode(0);}
if(engine.get_key_down("2")){setMode(1);}
if(engine.get_key_down("3")){setMode(2);}
if(engine.get_key_down("4")){setMode(3);}
if(engine.get_key_down("5")){setMode(4);}
if(engine.get_key_down("6")){setMode(5);}
if(engine.get_key_down("7")){setMode(6);}
if(mode==2){engine.html_set("Stamina",engine.to_string_i((30+ticks%70) as i64));}
if(mode==3 || mode==6){select(ticks%12);}
if(mode==5 && ticks%10==0){engine.html_load("assets/Ui/main.html");setMode(5);reloads++;}
let action:string=engine.html_event();
while(action!="") {
EVENT_BODY
if(action=="use_selected"){uses++;}
if(action=="close_inventory"){setMode(1);}
if(action=="open_inventory"){setMode(4);}
action=engine.html_event();
}
engine.set_global("audit_ticks",ticks);engine.set_global("audit_clicks",clicks);
engine.set_global("audit_uses",uses);engine.set_global("audit_reloads",reloads);
}
'''
    events = ''.join(f'if(action=="select_{i}"){{select({i});clicks++;}}' for i in range(12))
    driver = driver.replace('SELECT_BODY', '\n'.join(select)).replace('EVENT_BODY', events)
    driver = driver.replace('INITIAL_MODE', '0' if args.defer_ui else '4')
    if args.defer_ui:
        # Avoid the host's conventional main.html startup load as well as init's.
        (ui / 'main.html').rename(ui / 'audit.html')
        (ui / 'main.css').rename(ui / 'audit.css')
        driver = driver.replace('assets/Ui/main.html', 'assets/Ui/audit.html')
    (scripts / 'audit.ts').write_text(driver, encoding='utf-8')
    config = f'(title:"Weva runtime audit",width:{args.viewport[0]},height:{args.viewport[1]},fullscreen:false,vsync:false,' \
             'start_scene:"Scenes/audit.scene.ron",show_cursor:true,agent_activity_overlay:false,' \
             f'threaded_rendering:{str(not args.direct).lower()})'
    (output / 'assets/GameConfig.ron').write_text(config, encoding='utf-8')
    identity = 'transform:(position:(0.0,0.0,0.0),rotation:(0.0,0.0,0.0,1.0),scale:(1.0,1.0,1.0))'
    entities = ['(name:"AuditDriver",stable_id:Some("probe"),' + identity + ',script_list:Some((scripts:['
                '(script_ref:"Scripts/audit.ts",enabled:true)])))',
                '(name:"Camera",transform:(position:(-6.240842,5.67302,9.122213),'
                'rotation:(-0.18979605,-0.28962952,-0.058710802,0.93629336),scale:(1.0,1.0,1.0)),'
                'camera:Some((is_active:true,clear_color:(0.1,0.13,0.16,1.0),bloom_enabled:false)))',
                '(name:"Sun",' + identity + ',light:Some((kind:Directional,intensity:3.0)))']
    for i in range(240):
        x, z = (i % 20 - 10) * 1.2, (i // 20 - 6) * 1.2
        height = 0.3 + (i * 7 % 11) * 0.12
        entities.append(f'(name:"Cube{i}",transform:(position:({x:.3f},{height / 2:.3f},{z:.3f}),'
                        f'rotation:(0.0,0.0,0.0,1.0),scale:(0.8,{height:.3f},0.8)),mesh_renderer:Some((shape:Cube)))')
    (scenes / 'audit.scene.ron').write_text('(version:1,entities:[' + ','.join(entities) + '])', encoding='utf-8')
    (output / 'AgenticTools.ron').write_text('(profiles:{"dev":(runtime_features:["weva"])})', encoding='utf-8')
    (output / 'AUDIT.txt').write_text('Keys: 1=world only, 2=static HUD, 3=updating HUD, '
        '4=cycling inventory, 5=interactive inventory, 6=reload every 10 ticks, '
        '7=hidden inventory with continuing binding updates.\n'
        'Globals: audit_mode, audit_ticks, audit_selected, audit_clicks, audit_uses, audit_reloads.\n'
        'Synthetic 240-cube scene; not a production-game workload.\n', encoding='utf-8')
    print(output)


if __name__ == '__main__':
    main()
