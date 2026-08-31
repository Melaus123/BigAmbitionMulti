"""
hsg_reader.py -- reader for Big Ambitions .hsg save files.

FORMAT (derived from decompile):
  SaveGameManager.Save()  -> SerializeSaveGame() -> SaveGameSerializationHelper.SerializeBinaryData(path, inst, compressed:false)
                             = OdinSerializer SerializationUtility.SerializeValue(inst, stream, DataFormat.Binary)
                          -> CompressSaveGame() -> SaveGameSerializationHelper.CompressBinaryData()
                             = GZipStream(CompressionLevel.Optimal) over the raw binary
  So: .hsg == gzip( OdinSerializer DataFormat.Binary stream of GameInstance )
  Load path confirms: DeserializeBinaryData() = GZip decompress -> BinaryDataReader -> DeserializeValue<GameInstance>.

Odin binary entry encoding implemented below; validated by requiring a full-stream parse
that terminates on EndOfStream with zero trailing bytes.
"""
import gzip, struct, sys, io

# BinaryEntryType (OdinSerializer)
INVALID=0
NAMED_START_REF=1; UNNAMED_START_REF=2
NAMED_START_STRUCT=3; UNNAMED_START_STRUCT=4
END_OF_NODE=5
START_OF_ARRAY=6; END_OF_ARRAY=7
PRIMITIVE_ARRAY=8
NAMED_INTERNAL_REF=9; UNNAMED_INTERNAL_REF=10
NAMED_EXT_REF_INDEX=11; UNNAMED_EXT_REF_INDEX=12
NAMED_EXT_REF_GUID=13; UNNAMED_EXT_REF_GUID=14
NAMED_SBYTE=15; UNNAMED_SBYTE=16
NAMED_BYTE=17; UNNAMED_BYTE=18
NAMED_SHORT=19; UNNAMED_SHORT=20
NAMED_USHORT=21; UNNAMED_USHORT=22
NAMED_INT=23; UNNAMED_INT=24
NAMED_UINT=25; UNNAMED_UINT=26
NAMED_LONG=27; UNNAMED_LONG=28
NAMED_ULONG=29; UNNAMED_ULONG=30
NAMED_FLOAT=31; UNNAMED_FLOAT=32
NAMED_DOUBLE=33; UNNAMED_DOUBLE=34
NAMED_DECIMAL=35; UNNAMED_DECIMAL=36
NAMED_CHAR=37; UNNAMED_CHAR=38
NAMED_STRING=39; UNNAMED_STRING=40
NAMED_GUID=41; UNNAMED_GUID=42
NAMED_BOOL=43; UNNAMED_BOOL=44
NAMED_NULL=45; UNNAMED_NULL=46
TYPE_NAME=47; TYPE_ID=48
END_OF_STREAM=49
NAMED_EXT_REF_STRING=50; UNNAMED_EXT_REF_STRING=51

SCALAR = {
    NAMED_SBYTE:('b',1), UNNAMED_SBYTE:('b',1),
    NAMED_BYTE:('B',1),  UNNAMED_BYTE:('B',1),
    NAMED_SHORT:('<h',2),UNNAMED_SHORT:('<h',2),
    NAMED_USHORT:('<H',2),UNNAMED_USHORT:('<H',2),
    NAMED_INT:('<i',4),  UNNAMED_INT:('<i',4),
    NAMED_UINT:('<I',4), UNNAMED_UINT:('<I',4),
    NAMED_LONG:('<q',8), UNNAMED_LONG:('<q',8),
    NAMED_ULONG:('<Q',8),UNNAMED_ULONG:('<Q',8),
    NAMED_FLOAT:('<f',4),UNNAMED_FLOAT:('<f',4),
    NAMED_DOUBLE:('<d',8),UNNAMED_DOUBLE:('<d',8),
    NAMED_BOOL:('?',1),  UNNAMED_BOOL:('?',1),
}
NAMED = {NAMED_START_REF,NAMED_START_STRUCT,NAMED_INTERNAL_REF,NAMED_EXT_REF_INDEX,
         NAMED_EXT_REF_GUID,NAMED_SBYTE,NAMED_BYTE,NAMED_SHORT,NAMED_USHORT,NAMED_INT,
         NAMED_UINT,NAMED_LONG,NAMED_ULONG,NAMED_FLOAT,NAMED_DOUBLE,NAMED_DECIMAL,
         NAMED_CHAR,NAMED_STRING,NAMED_GUID,NAMED_BOOL,NAMED_NULL,NAMED_EXT_REF_STRING}


class Node(dict):
    """Reference/struct node: field name -> value, plus $type/$id/$items."""
    __slots__ = ()


class Reader:
    def __init__(self, data):
        self.d = data
        self.p = 0
        self.types = {}

    def u8(self):
        v = self.d[self.p]; self.p += 1; return v

    def i32(self):
        v = struct.unpack_from('<i', self.d, self.p)[0]; self.p += 4; return v

    def i64(self):
        v = struct.unpack_from('<q', self.d, self.p)[0]; self.p += 8; return v

    def string(self):
        flag = self.d[self.p]; self.p += 1
        n = struct.unpack_from('<i', self.d, self.p)[0]; self.p += 4
        if flag == 0:
            s = self.d[self.p:self.p+n].decode('latin-1'); self.p += n
        else:
            s = self.d[self.p:self.p+2*n].decode('utf-16-le'); self.p += 2*n
        return s

    def read_type(self):
        t = self.d[self.p]
        if t == TYPE_NAME:
            self.p += 1
            tid = self.i32()
            name = self.string()
            self.types[tid] = name
            return name
        if t == TYPE_ID:
            self.p += 1
            tid = self.i32()
            return self.types.get(tid, '<id%d>' % tid)
        if t == NAMED_NULL or t == UNNAMED_NULL:
            self.p += 1
            return None
        return None  # no type written

    # ---- entry-level parse ----
    def parse_value(self, e):
        """Read the payload of entry type e (name already consumed if named)."""
        if e in (NAMED_START_REF, UNNAMED_START_REF):
            t = self.read_type(); nid = self.i32()
            return self.parse_container(t, nid)
        if e in (NAMED_START_STRUCT, UNNAMED_START_STRUCT):
            t = self.read_type()
            return self.parse_container(t, None)
        if e == START_OF_ARRAY:
            n = self.i64()
            return self.parse_array(n)
        if e == PRIMITIVE_ARRAY:
            esz = self.i32(); cnt = self.i32()
            raw = self.d[self.p:self.p+esz*cnt]; self.p += esz*cnt
            return {'$primarray': (esz, cnt), 'bytes': raw}
        if e in SCALAR:
            f, sz = SCALAR[e]
            v = struct.unpack_from(f, self.d, self.p)[0]; self.p += sz
            return v
        if e in (NAMED_DECIMAL, UNNAMED_DECIMAL):
            raw = self.d[self.p:self.p+16]; self.p += 16
            return {'$decimal': raw.hex()}
        if e in (NAMED_CHAR, UNNAMED_CHAR):
            v = self.d[self.p:self.p+2].decode('utf-16-le'); self.p += 2
            return v
        if e in (NAMED_STRING, UNNAMED_STRING):
            return self.string()
        if e in (NAMED_GUID, UNNAMED_GUID):
            raw = self.d[self.p:self.p+16]; self.p += 16
            return {'$guid': raw.hex()}
        if e in (NAMED_NULL, UNNAMED_NULL):
            return None
        if e in (NAMED_INTERNAL_REF, UNNAMED_INTERNAL_REF):
            return {'$ref': self.i32()}
        if e in (NAMED_EXT_REF_INDEX, UNNAMED_EXT_REF_INDEX):
            return {'$extidx': self.i32()}
        if e in (NAMED_EXT_REF_GUID, UNNAMED_EXT_REF_GUID):
            raw = self.d[self.p:self.p+16]; self.p += 16
            return {'$extguid': raw.hex()}
        if e in (NAMED_EXT_REF_STRING, UNNAMED_EXT_REF_STRING):
            return {'$extstr': self.string()}
        raise ValueError('unhandled entry %d at %d' % (e, self.p-1))

    def parse_container(self, tname, nid):
        node = Node()
        node['$type'] = tname
        if nid is not None:
            node['$id'] = nid
        while True:
            e = self.u8()
            if e == END_OF_NODE:
                return node
            if e == END_OF_STREAM:
                self.p -= 1
                return node
            if e in NAMED:
                name = self.string()
                node[name] = self.parse_value(e)
            else:
                node.setdefault('$items', []).append(self.parse_value(e))

    def parse_array(self, n):
        out = []
        while True:
            e = self.u8()
            if e == END_OF_ARRAY:
                return out
            if e == END_OF_STREAM:
                self.p -= 1
                return out
            if e in NAMED:
                name = self.string()
                out.append((name, self.parse_value(e)))
            else:
                out.append(self.parse_value(e))

    def parse_root(self):
        e = self.u8()
        if e in NAMED:
            self.string()
        return self.parse_value(e)


def load_hsg(path):
    raw = gzip.open(path, 'rb').read()
    r = Reader(raw)
    root = r.parse_root()
    trailing = len(raw) - r.p
    # expect exactly one EndOfStream byte left, or zero
    tail = raw[r.p:]
    ok = (tail == b'' or tail == bytes([END_OF_STREAM]))
    return root, {'decompressed': len(raw), 'consumed': r.p, 'tail': tail.hex(),
                  'clean': ok, 'types': len(r.types)}


if __name__ == '__main__':
    root, info = load_hsg(sys.argv[1])
    print(info)
    print('root type', root.get('$type'))
    print('Day', root.get('Day'), 'Hour', root.get('Hour'), 'Minute', root.get('Minute'))
    keys = [k for k in root.keys()]
    print('root keys (%d):' % len(keys))
    for k in keys:
        v = root[k]
        kind = type(v).__name__
        if isinstance(v, list):
            kind = 'list[%d]' % len(v)
        elif isinstance(v, Node):
            kind = 'Node(%s)' % v.get('$type')
        print('   ', k, '=', kind if not isinstance(v, (int, float, str, bool, type(None))) else repr(v)[:80])
