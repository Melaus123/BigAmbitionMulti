"""Extract the '46 ba:street_fifthavenue' registration facts from a .hsg save."""
import sys, json
from hsg_reader import load_hsg, Node

STREET = 'ba:street_fifthavenue'
NUMBER = 46
DOW = ['Monday','Tuesday','Wednesday','Thursday','Friday','Saturday','Sunday']


def unwrap_list(v):
    """Odin List<T>/Dictionary nodes carry their payload in $items."""
    if isinstance(v, Node):
        it = v.get('$items')
        if it is None:
            return []
        if len(it) == 1 and isinstance(it[0], list):
            return it[0]
        return it
    if isinstance(v, list):
        return v
    return []


def find_reg(root, street=STREET, number=NUMBER):
    regs = unwrap_list(root['BuildingRegistrations'])
    for r in regs:
        if isinstance(r, Node) and r.get('StreetName') == street and r.get('StreetNumber') == number:
            return r, len(regs)
    return None, len(regs)


def deref(root_index, v):
    """Resolve Odin internal references ($ref -> node id)."""
    while isinstance(v, dict) and '$ref' in v:
        v = root_index.get(v['$ref'], v)
        if isinstance(v, dict) and '$ref' in v:
            break
    return v


def build_index(node, idx=None, seen=None):
    if idx is None: idx = {}
    if seen is None: seen = set()
    stack = [node]
    while stack:
        n = stack.pop()
        if isinstance(n, Node):
            if id(n) in seen: continue
            seen.add(id(n))
            if '$id' in n: idx[n['$id']] = n
            for k, v in n.items():
                if isinstance(v, (Node, list)): stack.append(v)
        elif isinstance(n, list):
            if id(n) in seen: continue
            seen.add(id(n))
            for v in n:
                if isinstance(v, (Node, list)): stack.append(v)
    return idx


def dict_pairs(v):
    """Odin Dictionary<K,V>: $items holds one unnamed struct node per entry with
    named fields '$k' and '$v'. Older/alternate layouts: flat alternating list."""
    items = unwrap_list(v)
    out = []
    if items and all(isinstance(x, Node) and '$k' in x for x in items):
        return [(x.get('$k'), x.get('$v')) for x in items]
    if items and all(isinstance(x, tuple) for x in items):
        return items
    for i in range(0, len(items) - 1, 2):
        out.append((items[i], items[i+1]))
    return out


def main(path, label):
    root, info = load_hsg(path)
    idx = build_index(root)
    reg, nregs = find_reg(root)
    out = {'label': label, 'path': path, 'parse': info,
           'Day': root.get('Day'), 'Hour': root.get('Hour'), 'Minute': root.get('Minute'),
           'characterId': root.get('characterId'), 'SaveGameName': root.get('SaveGameName'),
           'nRegistrations': nregs}
    if reg is None:
        out['error'] = 'registration not found'
        print(json.dumps(out, indent=1, default=str)); return

    reg = deref(idx, reg)
    out['BusinessName'] = reg.get('BusinessName')
    out['businessTypeName'] = reg.get('businessTypeName')
    out['RentedByPlayer'] = reg.get('RentedByPlayer')
    out['temporarilyClosed'] = reg.get('temporarilyClosed')
    out['takenOver'] = reg.get('takenOver')
    out['Layout'] = reg.get('Layout')
    out['blueprintName'] = reg.get('blueprintName')
    out['creationDay'] = reg.get('creationDay')
    out['customerCapacity'] = reg.get('customerCapacity')
    out['warnedLastHourAboutNoEmployee'] = reg.get('warnedLastHourAboutNoEmployee')
    out['buildingOwnerRivalId'] = reg.get('buildingOwnerRivalId')
    out['businessOwnerRivalId'] = reg.get('businessOwnerRivalId')
    out['regKeys'] = sorted([k for k in reg.keys()])

    # ---- schedule days ----
    days = []
    for sd in unwrap_list(reg.get('scheduleDays')):
        sd = deref(idx, sd)
        if not isinstance(sd, Node): continue
        d = {'dayRaw': sd.get('day'), 'isOpen': sd.get('isOpen'),
             'openingHourSlots': [], 'workShifts': []}
        try:
            d['day'] = DOW[int(sd.get('day'))]
        except Exception:
            d['day'] = str(sd.get('day'))
        for s in unwrap_list(sd.get('openingHourSlots')):
            s = deref(idx, s)
            if isinstance(s, Node):
                d['openingHourSlots'].append([s.get('startingHour'), s.get('endingHour')])
        for w in unwrap_list(sd.get('workShifts')):
            w = deref(idx, w)
            if isinstance(w, Node):
                d['workShifts'].append({'start': w.get('startingHour'), 'end': w.get('endingHour'),
                                        'employeeId': w.get('employeeId'),
                                        'itemInstanceId': w.get('itemInstanceId'),
                                        'type': w.get('type')})
        days.append(d)
    out['scheduleDays'] = days

    # ---- item instances ----
    items = {}
    for k, v in dict_pairs(reg.get('itemInstances')):
        v = deref(idx, v)
        key = k if isinstance(k, str) else str(k)
        if isinstance(v, Node):
            items[key] = {'id': v.get('id'), 'itemName': v.get('itemName'),
                          'alias': v.get('alias'), 'parentId': v.get('parentId'),
                          'stateIndex': v.get('stateIndex')}
        else:
            items[key] = {'raw': str(v)[:80]}
    out['nItems'] = len(items)
    out['items'] = items
    print(json.dumps(out, indent=1, default=str))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else '')
