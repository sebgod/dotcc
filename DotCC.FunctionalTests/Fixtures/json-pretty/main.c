/* A mini JSON parser + pretty-printer.
 *
 * Showcase of dotcc's breadth in one program: an enum tag, a tagged `union`
 * field, self-referential structs reached through pointers, recursive-descent
 * parsing, malloc/free of the whole tree (incl. pointer-to-pointer arrays and
 * a malloc'd array of structs), strtod, and recursive printing. No escape
 * handling in strings — the input below avoids them.
 *
 * (Note: the child arrays are malloc'd POINTER fields, not array fields —
 * dotcc lowers a struct's array field to a C# fixed-buffer, which allows only
 * primitive element types, so an array-of-pointers/structs field can't be a
 * member. malloc'd pointers are the portable shape and exercise more of the
 * allocator surface anyway.) */
#include <stdio.h>
#include <stdlib.h>

#define CAP 16   /* max elements per array / members per object */

enum JType { J_NULL, J_BOOL, J_NUM, J_STR, J_ARR, J_OBJ };

struct JVal;   /* forward — referenced through pointers below */

struct JArr { struct JVal **items; int count; };
struct JMember { char *key; struct JVal *val; };
struct JObj { struct JMember *members; int count; };

union JData {
    int bval;            /* J_BOOL */
    double num;          /* J_NUM  */
    char *str;           /* J_STR  */
    struct JArr *arr;    /* J_ARR  */
    struct JObj *obj;    /* J_OBJ  */
};

struct JVal { enum JType type; union JData data; };

/* ---- parser (a single advancing cursor over the source) ---- */

const char *cur;

void skip_ws(void) {
    while (*cur == ' ' || *cur == '\t' || *cur == '\n' || *cur == '\r') { cur++; }
}

struct JVal *new_val(enum JType t) {
    struct JVal *v = (struct JVal *)malloc(sizeof(struct JVal));
    v->type = t;
    return v;
}

/* Parse a "double-quoted" run into a fresh malloc'd C string. */
char *parse_raw_string(void) {
    cur++;                       /* opening quote */
    const char *start = cur;
    int len = 0;
    while (*cur != 0 && *cur != '"') { cur++; len++; }
    char *s = (char *)malloc(len + 1);
    for (int i = 0; i < len; i++) { s[i] = start[i]; }
    s[len] = 0;
    cur++;                       /* closing quote */
    return s;
}

struct JVal *parse_value(void);  /* forward */

struct JVal *parse_string(void) {
    struct JVal *v = new_val(J_STR);
    v->data.str = parse_raw_string();
    return v;
}

struct JVal *parse_number(void) {
    char *end;
    double n = strtod(cur, &end);
    cur = end;
    struct JVal *v = new_val(J_NUM);
    v->data.num = n;
    return v;
}

struct JVal *parse_array(void) {
    cur++;                       /* '[' */
    struct JArr *a = (struct JArr *)malloc(sizeof(struct JArr));
    a->items = (struct JVal **)malloc(CAP * sizeof(struct JVal *));
    a->count = 0;
    skip_ws();
    if (*cur == ']') { cur++; }
    else {
        while (1) {
            a->items[a->count++] = parse_value();
            skip_ws();
            if (*cur == ',') { cur++; continue; }
            if (*cur == ']') { cur++; }
            break;
        }
    }
    struct JVal *v = new_val(J_ARR);
    v->data.arr = a;
    return v;
}

struct JVal *parse_object(void) {
    cur++;                       /* '{' */
    struct JObj *o = (struct JObj *)malloc(sizeof(struct JObj));
    o->members = (struct JMember *)malloc(CAP * sizeof(struct JMember));
    o->count = 0;
    skip_ws();
    if (*cur == '}') { cur++; }
    else {
        while (1) {
            skip_ws();
            int k = o->count;
            o->members[k].key = parse_raw_string();
            skip_ws();
            cur++;               /* ':' */
            o->members[k].val = parse_value();
            o->count++;
            skip_ws();
            if (*cur == ',') { cur++; continue; }
            if (*cur == '}') { cur++; }
            break;
        }
    }
    struct JVal *v = new_val(J_OBJ);
    v->data.obj = o;
    return v;
}

struct JVal *parse_value(void) {
    skip_ws();
    char c = *cur;
    if (c == '"') { return parse_string(); }
    if (c == '[') { return parse_array(); }
    if (c == '{') { return parse_object(); }
    if (c == 't') { cur += 4; struct JVal *v = new_val(J_BOOL); v->data.bval = 1; return v; }
    if (c == 'f') { cur += 5; struct JVal *v = new_val(J_BOOL); v->data.bval = 0; return v; }
    if (c == 'n') { cur += 4; return new_val(J_NULL); }
    return parse_number();
}

/* ---- pretty-printer ---- */

void indent(int depth) {
    for (int i = 0; i < depth; i++) { printf("  "); }
}

void print_value(struct JVal *v, int depth) {
    switch (v->type) {
        case J_NULL:
            printf("null");
            break;
        case J_BOOL:
            printf(v->data.bval ? "true" : "false");
            break;
        case J_NUM: {
            double n = v->data.num;
            if (n == (double)(int)n) { printf("%d", (int)n); }  /* integral → no decimals */
            else { printf("%g", n); }
            break;
        }
        case J_STR:
            printf("\"%s\"", v->data.str);
            break;
        case J_ARR: {
            struct JArr *a = v->data.arr;
            if (a->count == 0) { printf("[]"); break; }
            printf("[\n");
            for (int i = 0; i < a->count; i++) {
                indent(depth + 1);
                print_value(a->items[i], depth + 1);
                printf(i + 1 < a->count ? ",\n" : "\n");
            }
            indent(depth);
            printf("]");
            break;
        }
        case J_OBJ: {
            struct JObj *o = v->data.obj;
            if (o->count == 0) { printf("{}"); break; }
            printf("{\n");
            for (int i = 0; i < o->count; i++) {
                indent(depth + 1);
                printf("\"%s\": ", o->members[i].key);
                print_value(o->members[i].val, depth + 1);
                printf(i + 1 < o->count ? ",\n" : "\n");
            }
            indent(depth);
            printf("}");
            break;
        }
    }
}

/* ---- recursive teardown ---- */

void free_value(struct JVal *v) {
    if (v->type == J_STR) {
        free(v->data.str);
    } else if (v->type == J_ARR) {
        for (int i = 0; i < v->data.arr->count; i++) { free_value(v->data.arr->items[i]); }
        free(v->data.arr->items);
        free(v->data.arr);
    } else if (v->type == J_OBJ) {
        for (int i = 0; i < v->data.obj->count; i++) {
            free(v->data.obj->members[i].key);
            free_value(v->data.obj->members[i].val);
        }
        free(v->data.obj->members);
        free(v->data.obj);
    }
    free(v);
}

int main(void) {
    cur = "{\"name\":\"dotcc\",\"version\":3,\"langs\":[\"C\",\"C#\"],"
          "\"flags\":{\"aot\":true,\"unsafe\":true},\"ratio\":1.5,\"nothing\":null}";
    struct JVal *root = parse_value();
    print_value(root, 0);
    printf("\n");
    free_value(root);
    return 0;
}
