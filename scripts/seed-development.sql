INSERT INTO customers (name, address_line_1, address_line_2, city, state, postal_code, is_active)
SELECT seed.name, seed.address_line_1, seed.address_line_2, seed.city, seed.state, seed.postal_code, seed.is_active
FROM (
    VALUES
        ('Acme Industrial Supply', '100 Market Street', NULL, 'Philadelphia', 'PA', '19106', true),
        ('Northwind Manufacturing', '42 Harbor Avenue', 'Building 3', 'Baltimore', 'MD', '21224', true),
        ('Contoso Tooling', '725 Foundry Road', NULL, 'Pittsburgh', 'PA', '15222', false)
) AS seed(name, address_line_1, address_line_2, city, state, postal_code, is_active)
WHERE NOT EXISTS (SELECT 1 FROM customers);

INSERT INTO parts (customer_id, part_number, revision, description)
SELECT customer.id, seed.part_number, seed.revision, seed.description
FROM (
    VALUES
        ('Acme Industrial Supply', 'ACME-100', 'A', 'Stainless steel shoulder bolt'),
        ('Acme Industrial Supply', 'ACME-200', NULL, 'Low-profile socket cap screw'),
        ('Northwind Manufacturing', 'NW-4500', '2', 'Custom threaded insert')
) AS seed(customer_name, part_number, revision, description)
JOIN customers AS customer ON customer.name = seed.customer_name
ON CONFLICT (customer_id, part_number) DO NOTHING;
