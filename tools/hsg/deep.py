"""Deeper extraction: items, employees, owned-shop census, orphan check."""
import sys, json
from hsg_reader import load_hsg, Node
from shop import unwrap_list, dict_pairs, build_index, deref, DOW

STATION = 'XbgJIZVHjEKUsu3P2Jzw=='

def reg_key(r):
    return '%d %s' % (r.get('StreetNumber'), r.get('StreetName'))

def main(path, label):
    root, info = load_hsg(path)
    idx = build_index(root)
    regs = unwrap_list(root['BuildingRegistrations'])

    out = {'label': label, 'Day': root.get('Day'), 'Hour': root.get('Hour'),
           'characterId': root.get('characterId'), 'parse_clean': info['clean'],
           'nRegs': len(regs)}

    # census: registrations that are rented/owned and how many itemInstances they carry
    owned = []
    with_items = []
    total_items = 0
    target = None
    for r in regs:
        r = deref(idx, r)
        if not isinstance(r, Node): continue
        pairs = dict_pairs(r.get('itemInstances'))
        n = len(pairs)
        total_items += n
        if r.get('RentedByPlayer'):
            owned.append({'k': reg_key(r), 'name': r.get('BusinessName'),
                          'type': r.get('businessTypeName'), 'items': n,
                          'tempClosed': r.get('temporarilyClosed'),
                          'shifts': sum(len(unwrap_list(deref(idx, sd).get('workShifts')))
                                        for sd in unwrap_list(r.get('scheduleDays'))
                                        if isinstance(deref(idx, sd), Node))})
        if n:
            with_items.append((reg_key(r), r.get('BusinessName'), n, bool(r.get('RentedByPlayer'))))
        if r.get('StreetName') == 'ba:street_fifthavenue' and r.get('StreetNumber') == 46:
            target = r
    out['ownedByPlayer'] = owned
    out['regsWithItems'] = len(with_items)
    out['totalItemInstances'] = total_items
    out['withItemsList'] = with_items[:60]

    # employees
    emps = {}
    empkeys = None
    for e in unwrap_list(root.get('EmployeeInstances')):
        e = deref(idx, e)
        if isinstance(e, Node):
            if empkeys is None:
                empkeys = sorted(k for k in e.keys())
            emps[e.get('id')] = {'firstName': e.get('firstName'), 'lastName': e.get('lastName'),
                                 'StreetName': e.get('StreetName'), 'StreetNumber': e.get('StreetNumber'),
                                 'streetName': e.get('streetName'), 'streetNumber': e.get('streetNumber'),
                                 'skillName': e.get('skillName'), 'fired': e.get('fired'),
                                 'isFired': e.get('isFired'), 'sick': e.get('sick'),
                                 'quit': e.get('quit'), 'onVacation': e.get('onVacation'),
                                 'daysOfSickLeaveLeft': e.get('daysOfSickLeaveLeft'),
                                 'stress': e.get('stress'), 'morale': e.get('morale')}
    out['nEmployeeInstances'] = len(emps)
    out['employeeFieldNames'] = empkeys
    out['employeeIds'] = sorted(k for k in emps if k)
    out['employeeRecords'] = emps
    cands = {}
    for e in unwrap_list(root.get('CandidateEmployeeInstances')):
        e = deref(idx, e)
        if isinstance(e, Node):
            cands[e.get('id')] = True
    out['nCandidates'] = len(cands)
    out['candidateIds'] = sorted(k for k in cands if k)

    if target is None:
        out['target'] = None
        print(json.dumps(out, indent=1, default=str)); return

    t = {'BusinessName': target.get('BusinessName'),
         'RentedByPlayer': target.get('RentedByPlayer'),
         'temporarilyClosed': target.get('temporarilyClosed'),
         'takenOver': target.get('takenOver'),
         'buildingOwnerRivalId': target.get('buildingOwnerRivalId'),
         'businessOwnerRivalId': target.get('businessOwnerRivalId'),
         'AvailableForRent': target.get('AvailableForRent'),
         'creationDay': target.get('creationDay'),
         'lastDeposit': target.get('lastDeposit'),
         'customerCapacity': target.get('customerCapacity'),
         'nRetailPrices': len(unwrap_list(target.get('retailPrices'))),
         'nStoredRetailPrices': len(unwrap_list(target.get('storedRetailPrices'))),
         'nInteriorDesigns': len(unwrap_list(target.get('interiorDesigns'))),
         'nOrderHistory': len(unwrap_list(target.get('orderHistory'))),
         'nAiEmployees': len(unwrap_list(target.get('aiEmployees'))),
         'nDirtSpots': len(unwrap_list(target.get('dirtSpots'))),
         'nCachedAvailableProducts': len(unwrap_list(target.get('cachedAvailableProducts'))),
         'dailyIncomes': unwrap_list(target.get('dailyIncomes'))[-6:],
         }
    items = {}
    for k, v in dict_pairs(target.get('itemInstances')):
        v = deref(idx, v)
        if isinstance(v, Node):
            items[str(k)] = {'id': v.get('id'), 'itemName': v.get('itemName'),
                             'alias': v.get('alias'), 'stateIndex': v.get('stateIndex'),
                             'parentId': v.get('parentId')}
    t['nItems'] = len(items)
    t['items'] = items

    # interiorDesigns payload (SerializedInteriorDesign) - does it hold item ids?
    ids_in_designs = []
    for d in unwrap_list(target.get('interiorDesigns')):
        d = deref(idx, d)
        if isinstance(d, Node):
            ids_in_designs.append({k: (str(v)[:120] if not isinstance(v, (Node, list)) else
                                       ('Node' if isinstance(v, Node) else 'list%d' % len(v)))
                                   for k, v in d.items()})
    t['interiorDesigns'] = ids_in_designs

    # shift ids vs item ids
    shift_ids = set(); shift_emps = set(); sched = []
    for sd in unwrap_list(target.get('scheduleDays')):
        sd = deref(idx, sd)
        if not isinstance(sd, Node): continue
        ws = []
        for w in unwrap_list(sd.get('workShifts')):
            w = deref(idx, w)
            if isinstance(w, Node):
                shift_ids.add(w.get('itemInstanceId')); shift_emps.add(w.get('employeeId'))
                ws.append([w.get('startingHour'), w.get('endingHour'), w.get('employeeId'),
                           w.get('itemInstanceId'), w.get('type')])
        sched.append({'dayRaw': sd.get('day'), 'isOpen': sd.get('isOpen'),
                      'slots': [[deref(idx, s).get('startingHour'), deref(idx, s).get('endingHour')]
                                for s in unwrap_list(sd.get('openingHourSlots'))],
                      'shifts': ws})
    t['scheduleDays'] = sched
    item_ids = set(v['id'] for v in items.values()) | set(items.keys())
    t['shiftItemIds'] = sorted(x for x in shift_ids if x)
    t['orphanShiftItemIds'] = sorted(x for x in shift_ids if x and x not in item_ids)
    t['stationInItems'] = STATION in item_ids
    t['stationInShifts'] = STATION in shift_ids
    t['shiftEmployeeIds'] = sorted(x for x in shift_emps if x)
    t['shiftEmployeesPresent'] = {e: (e in emps) for e in shift_emps if e}
    t['shiftEmployeeRecords'] = {e: emps.get(e) for e in shift_emps if e and e in emps}
    out['target'] = t
    print(json.dumps(out, indent=1, default=str))

if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
